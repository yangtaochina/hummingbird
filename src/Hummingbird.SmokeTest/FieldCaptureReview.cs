using System.Security.Cryptography;
using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;

/// <summary>离线采集诊断；不创建设备，不保存可运行路线或加工目标。</summary>
internal static class FieldCaptureReview
{
    internal sealed record RoiProposal(string Name,double Px,double Py,double RadiusPx);
    public static int Run(string source,string output,string? roiProposalFile)
    {
        var session=RingScanSession.Load(Path.GetFullPath(source));
        var folder=Path.GetDirectoryName(Path.GetFullPath(source))!;
        var rings=session.Frames.Where(f=>f.Role==RingFrameRole.RingArc).ToList();
        var ringReviews=new List<object>();
        void ReviewRing(string name,List<RingScanFrame> frames)
        {
            string? rejection=null;
            try {new RingScanSolver().Analyze(new RingScanSession {Settings=session.Settings,Frames=frames});}
            catch(InvalidDataException ex){rejection=ex.Message;}
            // Only expose residuals after failure, never treat this diagnostic fit as accepted.
            var diagnostic=JsonSerializer.Deserialize<RingScanSettings>(JsonSerializer.Serialize(session.Settings))!;
            diagnostic.MaxRms=10;diagnostic.MaxSensitivity=10;
            try
            {
                var fit=new RingScanSolver().Analyze(new RingScanSession {Settings=diagnostic,Frames=frames});
                ringReviews.Add(new {Name=name,OriginalThresholdsPassed=rejection is null,Rejection=rejection,
                    DiagnosticFitOnly=true,fit.CenterX,fit.CenterY,fit.RadiusMm,fit.RmsMm,fit.SensitivityMm,
                    Views=fit.Views.Select((v,i)=>new {v.Name,v.Count,v.RmsMm,
                        MeanSignedResidualMm=fit.Points.Where(p=>p.FrameIndex==i).Average(p=>
                            double.Hypot(p.X-fit.CenterX,p.Y-fit.CenterY)-fit.RadiusMm)}),
                    Images=frames.Select(f=>new {f.Name,f.ImagePath})});
            }
            catch(InvalidDataException ex){ringReviews.Add(new {Name=name,OriginalThresholdsPassed=false,
                Rejection=rejection,DiagnosticRejection=ex.Message});}
        }
        string CandidateDirectory(RingScanFrame frame)
        {
            var dirs=Directory.GetDirectories(folder,"曝光选优_"+frame.Name+"_*");
            if(dirs.Length!=1)throw new InvalidDataException("曝光候选目录不唯一："+frame.Name);
            return dirs[0];
        }
        int[] Exposures(RingScanFrame frame)=>Directory.GetFiles(CandidateDirectory(frame),"*us.bmp")
            .Select(p=>int.Parse(Path.GetFileNameWithoutExtension(p).Replace("us",""))).Order().ToArray();
        RingScanFrame Candidate(RingScanFrame frame,int exposure)=>RingScanSession.Load(Path.Combine(
            CandidateDirectory(frame),exposure+"us.capture.json")).Frames.Single() with {Name=frame.Name,Role=frame.Role,FaceSeed=null};
        ReviewRing("OriginalSelectedFrames",rings);
        foreach(int exposure in rings.Select(Exposures).Aggregate((a,b)=>a.Intersect(b).ToArray()))
            ReviewRing("All_"+exposure+"us",rings.Select(f=>Candidate(f,exposure)).ToList());
        for(int i=0;i<rings.Count;i++)
        {
            foreach(int exposure in Exposures(rings[i]))
            {
                var variant=rings.ToList();variant[i]=Candidate(rings[i],exposure);
                ReviewRing("Replace_"+rings[i].Name+"_"+exposure+"us",variant);
            }
        }
        var references=new List<object>();
        var proposals=roiProposalFile is null ? [] : JsonSerializer.Deserialize<RoiProposal[]>(File.ReadAllText(roiProposalFile))!;
        foreach(var frame in session.Frames.Where(f=>f.Role!=RingFrameRole.RingArc))
        {
            var original=new DatumDirectionSolver().Inspect(frame,session.Settings,session.DirectionOptions);
            references.Add(new {Name=frame.Name,Exposure="Original",OperatorConfirmed=false,Inspection=original});
            // These are image-only ROI proposals, never operator confirmation or production input.
            var proposal=proposals.SingleOrDefault(p=>p.Name==frame.Name);
            if(proposal is null)continue;
            foreach(int exposure in Exposures(frame))
            foreach(double radius in new[]{-20.0,-10,0,10,20}.Select(d=>proposal.RadiusPx+d))
            {
                var candidate=Candidate(frame,exposure);
                candidate=candidate with {FaceSeed=new DatumCircleSeed(proposal.Px,proposal.Py,radius,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidate.ImagePath))),
                    "OfflineRoiProposal_NotOperatorConfirmed")};
                var inspection=new DatumDirectionSolver().Inspect(candidate,session.Settings,session.DirectionOptions);
                references.Add(new {Name=frame.Name,Exposure=exposure.ToString(),OperatorConfirmed=false,
                    Inspection=inspection});
            }
        }
        File.WriteAllText(Path.GetFullPath(output),JsonSerializer.Serialize(new {
            Kind="OfflineFieldCaptureReview",GeneratedAt=DateTimeOffset.Now,PreviewOnly=true,
            HardwareAccess=false,OperatorConfirmed=false,ProductionTargetsGenerated=false,
            Source=Path.GetFullPath(source),OriginalSettings=session.Settings,
            DiagnosticWarning="Diagnostic fits after rejection use relaxed limits solely to expose residuals; no production acceptance. ROI proposals are unconfirmed.",
            RingReviews=ringReviews,ReferenceReviews=references
        },new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine($"Saved offline diagnostics: {ringReviews.Count} ring variants, {references.Count} reference inspections. No hardware access.");
        return 0;
    }
}
