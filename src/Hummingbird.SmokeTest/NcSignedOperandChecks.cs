using System.Globalization;
using System.Text.RegularExpressions;
using Hummingbird.Core;

// 回归现场12180：检查生成文本中的有符号操作数，不模拟或宣称通过真实NC解释器。
internal static class NcSignedOperandChecks
{
    public static void Run(Action<bool,string> check)
    {
        bool Chained(string code) => Regex.IsMatch(code,@"[+\-*/<>=]\s*[+\-]");
        bool Balanced(string code)
        {
            int depth=0;
            foreach(char c in code) {if(c=='(')depth++;if(c==')' && --depth<0)return false;}
            return depth==0;
        }
        check(Chained("IF ABS($AA_IM[Z]--201.873240)>0.010 GOTOF HM_ERROR"),
            "捕获现场截图第7行未括起负数的12180回归样例");
        var previous=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");
            var cases=new[]
            {
                ("现场负XYZ",new NcTravelEnvelope(-160.4,-134,-209.6,-181.7,-201.87324,254.8245)),
                ("零Z跨零XY",new NcTravelEnvelope(-1,1,-2,2,0,0)),
                ("正XYZ",new NcTravelEnvelope(1,2,3,4,5,0)),
                ("舍入负零Z",new NcTravelEnvelope(-2,-1,3,4,-0.0000001,0))
            };
            foreach(var (name,bounds) in cases)
            {
                string nc=ContinuousNcProgram.Generate(bounds,161339);
                var lines=nc.Split('\n').Select(l=>l.Split(';')[0]).ToArray();
                check(lines.All(l=>!Chained(l) && Balanced(l)),name+"主控表达式无连续一元/二元运算符且括号配对");
                string z=bounds.Z.ToString("F6",CultureInfo.InvariantCulture);
                string guard=$"IF ABS($AA_IM[Z]-({z}))>0.010 GOTOF HM_ERROR";
                check(lines.Count(l=>l.Trim()==guard)==2 && nc.Contains($"IF R227<({bounds.MinX.ToString("F6",CultureInfo.InvariantCulture)})") &&
                    nc.Contains($"IF R228>({bounds.MaxY.ToString("F6",CultureInfo.InvariantCulture)})"),
                    name+"启动及运动前保留原Z保护、边界数值和小数点格式");
                var anchor=new CaptureWaypoint("test",RingFrameRole.Reference1,bounds.MinX,bounds.MinY,bounds.Z,bounds.C);
                string micro=XyMicroMoveSession.Program(anchor,161339);
                check(micro.Split('\n').Select(l=>l.Split(';')[0]).All(l=>!Chained(l) && Balanced(l)),
                    name+"微调的起点和累计位移保护无同类链式错误");
            }
        }
        finally{CultureInfo.CurrentCulture=previous;}
    }
}
