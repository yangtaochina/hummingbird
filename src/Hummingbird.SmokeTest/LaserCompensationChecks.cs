using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hummingbird.Core;
using Hummingbird.Vision;

internal static class LaserCompensationChecks
{
    public static int Run(string? sitePath)
    {
        int passed=0,failed=0;
        void Check(bool ok,string name) {Console.WriteLine($"[{(ok?"PASS":"FAIL")}] {name}");if(ok)passed++;else failed++;}
        void Reject(Action action,string name)
        {
            try {action();Check(false,name);}
            catch(Exception ex) when(ex is InvalidDataException or FileNotFoundException) {Check(true,name);}
        }
        string folder=Path.Combine(Path.GetTempPath(),"hummingbird-laser-compensation-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var settings=new RingScanSettings {Jxx=600,Jxy=-120,Jyx=80,Jyy=580};
            string before=Path.Combine(folder,"before.bmp"),after=Path.Combine(folder,"after.bmp"),trial=Path.Combine(folder,"trial.json");
            byte[] pixels=new byte[1920*1200];Array.Fill(pixels,(byte)80);
            RingScanSolver.SaveFrame(before,new CameraFrame(pixels,1920,1200,DateTime.Now));
            for(int y=470;y<546;y++)for(int x=1125;x<1202;x++)pixels[y*1920+x]=10;
            RingScanSolver.SaveFrame(after,new CameraFrame(pixels,1920,1200,DateTime.Now));
            var position=new {X=-20.0,Y=45.0,Z=-7.0,C=359.999};
            string original=JsonSerializer.Serialize(new {
                Version=1,DeviceMode="Real",DryRun=false,Completed=true,EmissionRequested=true,ShutdownConfirmed=true,Error=(string?)null,
                SoftwareOpticalSettings=settings,StartPosition=position,EndPosition=position,
                BeforeImage=new {FileName="before.bmp",Sha256=LaserSpotCalibration.Hash(before),Width=1920,Height=1200},
                AfterImage=new {FileName="after.bmp",Sha256=LaserSpotCalibration.Hash(after),Width=1920,Height=1200}
            });
            File.WriteAllText(trial,original);
            // Independent forward construction: known +0.3/-0.2mm produces +204/-92px with this J.
            var calibration=new LaserSpotCalibration {
                SourceTrialPath=trial,SourceTrialSha256=LaserSpotCalibration.Hash(trial),
                BeforeImagePath=before,BeforeImageSha256=LaserSpotCalibration.Hash(before),
                AfterImagePath=after,AfterImageSha256=LaserSpotCalibration.Hash(after),OpticalSettings=settings,
                PixelX=959.5+204,PixelY=599.5-92,ReferenceZ=-7,ReferenceC=359.999,Method="synthetic known displacement"
            };
            calibration.VerifySources();Check(true,"真实完成记录及前后原图哈希通过来源核对");
            var offset=LaserSpotCompensation.Offset(calibration,settings);
            Check(Math.Abs(offset.X-.3)<1e-12 && Math.Abs(offset.Y+.2)<1e-12,"含交叉项的独立真值恢复正确补偿正负号");
            var template=HolePatternTemplate.LoadJson("templates/dial_18holes.json");
            var targets=template.Holes.Select(h=>new HoleTarget(h.Id,h.X-20,h.Y+45,h.DiameterMm,h.Clock)).ToArray();
            var preview=LaserSpotCompensation.Preview(calibration,settings,-7,.001,targets);
            Check(preview.Targets.Count==18 && preview.PreviewOnly && preview.Calibration.PreviewOnly,
                "18孔补偿单独输出且始终标记预览");
            bool forward=true;
            foreach(var t in preview.Targets)
            {
                double dx=t.LaserCommandX-t.CameraX,dy=t.LaserCommandY-t.CameraY;
                forward &= Math.Abs(959.5+600*dx-120*dy-calibration.PixelX)<1e-9 &&
                    Math.Abs(599.5+80*dx+580*dy-calibration.PixelY)<1e-9;
            }
            Check(forward,"逐孔用独立前向成像模型确认目标确实落到激光像素");
            Check(preview.Targets.Zip(targets).All(pair=>pair.First.CameraX==pair.Second.MachineX &&
                pair.First.CameraY==pair.Second.MachineY && pair.First.DiameterMm==pair.Second.DiameterMm && pair.First.Clock==pair.Second.Clock),
                "原相机坐标、CAD孔径及钟点标识不被改写");
            double distance=double.Hypot(targets[0].MachineX-targets[1].MachineX,targets[0].MachineY-targets[1].MachineY);
            Check(Math.Abs(distance-double.Hypot(preview.Targets[0].LaserCommandX-preview.Targets[1].LaserCommandX,
                preview.Targets[0].LaserCommandY-preview.Targets[1].LaserCommandY))<1e-10,"固定平移保持CAD孔距");
            var centered=calibration with {PixelX=959.5,PixelY=599.5};
            Check(LaserSpotCompensation.Offset(centered,settings)==(0.0,0.0),"像素参考使用(width-1)/2，不引入半像素偏差");
            Reject(()=>LaserSpotCompensation.Preview(calibration,settings,-7,359.999,[]),"空孔位不生成补偿预览");
            Reject(()=>LaserSpotCompensation.Preview(calibration,settings,-7,359.999,[targets[0] with {MachineX=double.NaN}]),"无效孔位数值拒绝");
            Reject(()=>(calibration with {PixelX=double.NaN}).Validate(),"无效落点像素拒绝");
            Reject(()=>(calibration with {PixelY=1200}).Validate(),"图像范围外落点拒绝");
            Reject(()=>(calibration with {Kind="RingScanSession"}).Validate(),"其他类型记录不能冒充落点标定");
            Reject(()=>calibration.ValidateOptics(new RingScanSettings {Jxx=601,Jxy=-120,Jyx=80,Jyy=580}),"旧XY矩阵不能混用");
            Reject(()=>calibration.ValidateOptics(new RingScanSettings {Jxx=600,Jxy=-120,Jyx=80,Jyy=580,ImageWidth=1600}),"图像尺寸变化拒绝");
            calibration.ValidateOptics(new RingScanSettings {Jxx=600,Jxy=-120,Jyx=80,Jyy=580,HintX=99,HintY=22});
            Check(true,"工件粗圆心变化不被误当作光学标定变化");
            Reject(()=>calibration.ValidateReference(-7.02,359.999),"不同Z工作平面拒绝");
            Reject(()=>calibration.ValidateReference(-7,.02),"不同固定C角拒绝");
            calibration.ValidateReference(-7,.001);Check(true,"C跨360度零点正确比较");
            string saved=Path.Combine(folder,"calibration.json");calibration.Save(saved);
            var reloaded=LaserSpotCalibration.Load(saved);
            Check(reloaded.PixelX==calibration.PixelX && Path.IsPathFullyQualified(reloaded.AfterImagePath) &&
                !JsonNode.Parse(File.ReadAllText(saved))!["AfterImagePath"]!.GetValue<string>().Contains(folder),
                "标定采用相对来源路径保存，重载核对证据");
            Reject(()=>(calibration with {AfterImagePath=before}).VerifySources(),"原图路径错配拒绝");
            Reject(()=>(calibration with {BeforeImageSha256=calibration.AfterImageSha256}).VerifySources(),"哈希错配不接受");
            byte[] afterBytes=File.ReadAllBytes(after);File.AppendAllText(after,"changed");
            Reject(calibration.VerifySources,"原图覆盖后来源校验失败");File.WriteAllBytes(after,afterBytes);
            File.AppendAllText(trial," ");Reject(calibration.VerifySources,"试打记录内容变化不沿用旧来源");File.WriteAllText(trial,original);
            LaserSpotCalibration Changed(Action<JsonNode> edit)
            {
                var node=JsonNode.Parse(original)!;edit(node);File.WriteAllText(trial,node.ToJsonString());
                return calibration with {SourceTrialSha256=LaserSpotCalibration.Hash(trial)};
            }
            Reject(()=>Changed(n=>n["DryRun"]=true).VerifySources(),"演练即使更新哈希也不能冒充出光来源");
            Reject(()=>Changed(n=>n["ShutdownConfirmed"]=false).VerifySources(),"关光未确认记录不能成为来源");
            Reject(()=>Changed(n=>n["Completed"]=false).VerifySources(),"失败试打记录拒绝");
            Reject(()=>Changed(n=>n["DeviceMode"]="Sim").VerifySources(),"模拟设备记录拒绝");
            Reject(()=>Changed(n=>n["EndPosition"]!["X"]=-19.99).VerifySources(),"试打前后轴不稳定拒绝");
            Reject(()=>Changed(n=>n["SoftwareOpticalSettings"]!["Jxx"]=601).VerifySources(),"来源中的光学参数错配拒绝");
            Reject(()=>Changed(n=>n["AfterImage"]!["Width"]=1600).VerifySources(),"来源原图尺寸错配拒绝");
            File.WriteAllText(trial,original);
            Reject(()=>(calibration with {ReferenceZ=-6}).VerifySources(),"候选Z不能与真实试打Z不符");
            string program=LaserSpotCompensation.RecenterTrial(calibration,settings);
            Check(program.Contains("N30 SUPA G1 X-20.3000 Y45.2000 F6") && program.Contains("N50 SUPA G1 X-20.0000 Y45.0000 F6"),
                "复核用SUPA绝对MCS先减补偿居中，再返回原试打MCS");
            var moves=Regex.Matches(program,@"SUPA G1 X(?<x>-?[0-9.]+) Y(?<y>-?[0-9.]+)");
            Check(moves.Count==2 && decimal.Parse(moves[1].Groups["x"].Value,CultureInfo.InvariantCulture)==-20m &&
                decimal.Parse(moves[1].Groups["y"].Value,CultureInfo.InvariantCulture)==45m && !program.Contains("G91"),
                "NC使用明确的原始MCS返回点，不继承当前增量或工件旋转坐标");
            string[] executable=program.Split('\n').Where(l=>l.StartsWith('N')).ToArray();
            Check(!executable.Any(l=>Regex.IsMatch(l,@"\b[ZC][-0-9]|\bM[34]\b")) && executable.All(l=>!l.Contains("M0") || l.Contains("G90") || l.StartsWith("N20")),
                "复核文本不移动Z/C、不含激光开启指令，暂停前恢复绝对模式");
            Reject(()=>LaserSpotCompensation.RecenterTrial(centered,settings),"零偏移无需导出运动复核");
            Reject(()=>LaserSpotCompensation.RecenterTrial(calibration with {PixelX=1900,PixelY=1100},settings),"大范围补偿不导出小范围试走");
            Check(JsonSerializer.Deserialize<LaserCompensationPreview>(JsonSerializer.Serialize(preview))!.Targets.Count==18,
                "预览报告保留来源、参考Z/C及补偿前后坐标");
            if(sitePath is not null)
            {
                var site=LaserSpotCalibration.Load(Path.GetFullPath(sitePath));
                var value=LaserSpotCompensation.Offset(site,site.OpticalSettings);
                Check(Math.Abs(value.X-.32961391187577804)<1e-10 && Math.Abs(value.Y-.36090938030257225)<1e-10,
                    "本次12:10真实资料通过哈希校验且与独立Python计算一致");
                Check(LaserSpotCompensation.RecenterTrial(site,site.OpticalSettings).Contains("X-138.8886 Y-191.3650"),
                    "现场复核程序方向与居中预测一致");
                string nc=Path.Combine(Path.GetDirectoryName(site.SourceTrialPath)!,"HM_SPOT_CHECK.MPF");
                File.WriteAllText(nc,LaserSpotCompensation.RecenterTrial(site,site.OpticalSettings),System.Text.Encoding.ASCII);
                Console.WriteLine($"SITE candidate=({site.PixelX:F4},{site.PixelY:F4}) offset=({value.X:F8},{value.Y:F8}); exported {nc}");
            }
        }
        catch(Exception ex) {Console.WriteLine(ex);failed++;}
        finally
        {
            foreach(string file in Directory.EnumerateFiles(folder))File.Delete(file);
            Directory.Delete(folder);
        }
        Console.WriteLine($"Laser compensation: {passed} passed, {failed} failed; no hardware accessed.");
        return failed==0?0:1;
    }
}
