using System;
using System.Text.RegularExpressions;

namespace BatchPdfPublisherLauncher
{
    internal sealed class CadStartupGate
    {
        private int _pid;
        private string _document;
        private TimeSpan _firstSeen;
        private TimeSpan? _quietSince;
        public static bool DeferLoad(string tianzhengName,int assemblyCount) => assemblyCount>0
            && !string.IsNullOrWhiteSpace(tianzhengName)
            && !string.Equals(tianzhengName,"无天正",StringComparison.Ordinal);
        public static bool MatchesRelease(string version,string release) => !string.IsNullOrWhiteSpace(release)
            && Regex.Match(version??"",@"^\d+\.\d+").Value==release;

        public bool Observe(int pid,string document,bool quiet,TimeSpan elapsed)
        {
            if(pid<=0){_quietSince=null;return false;}
            if(_pid!=pid){_pid=pid;_firstSeen=elapsed;_quietSince=null;_document=null;}
            if(!quiet||string.IsNullOrWhiteSpace(document)){_quietSince=null;return false;}
            if(_quietSince==null||_document!=document){_document=document;_quietSince=elapsed;}
            // A brief idle interval between startup LISP/ARX callbacks is not readiness.
            return elapsed-_firstSeen>=TimeSpan.FromSeconds(15)
                && elapsed-_quietSince.Value>=TimeSpan.FromSeconds(8);
        }
    }
}
