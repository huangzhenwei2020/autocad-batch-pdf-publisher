using System;
using System.Collections.Generic;
using System.Linq;
using BatchPdfPublisher.Models;

namespace BatchPdfPublisher.BuildingModel
{
    public sealed class OpeningLayoutEditing
    {
        public List<DoorWindowLayoutCell> Cells { get; private set; }
        public double Width { get; private set; }
        public double Height { get; private set; }
        public double LastDividerCoordinate { get; private set; }
        private readonly Stack<string> _undo=new Stack<string>();
        private readonly Stack<string> _redo=new Stack<string>();
        public OpeningLayoutEditing(double width,double height,IEnumerable<DoorWindowLayoutCell> cells)
        {Width=width;Height=height;Cells=DoorWindowElevationGeometryBuilder.ParseCellLayout(DoorWindowElevationGeometryBuilder.SerializeCellLayout(cells));}
        public bool Edit(Action<List<DoorWindowLayoutCell>> action,out string error)
        {
            error=null;var original=DoorWindowElevationGeometryBuilder.SerializeCellLayout(Cells);
            var candidate=DoorWindowElevationGeometryBuilder.ParseCellLayout(original);
            try {action(candidate);DoorWindowElevationGeometryBuilder.ValidateCellLayout(candidate,Width,Height);
                var tolerance=.05;
                if(!candidate.Any(c=>!c.IsDeleted))throw new InvalidOperationException("至少要保留一个门窗面板。");
                if(candidate.Count==0 || Math.Abs(candidate.Min(c=>c.Left))>tolerance || Math.Abs(candidate.Max(c=>c.Right)-Width)>tolerance
                    || Math.Abs(candidate.Min(c=>c.Bottom))>tolerance || Math.Abs(candidate.Max(c=>c.Top)-Height)>tolerance)
                    throw new InvalidOperationException("分格只能移动内部中梃，门窗外边框尺寸不能改变。");
                if(candidate.Any(c=>new[]{c.Left,c.Right,c.Bottom,c.Top}.Any(v=>double.IsNaN(v)||double.IsInfinity(v))))throw new InvalidOperationException("分格尺寸无效。");
            }catch(Exception ex){error=ex.Message;return false;}
            if(original==DoorWindowElevationGeometryBuilder.SerializeCellLayout(candidate))return true;
            _undo.Push(original);_redo.Clear();Cells=candidate;return true;
        }
        public bool Undo(bool redo=false)
        {
            var source=redo ? _redo : _undo;var target=redo ? _undo : _redo;
            if(source.Count==0)return false;target.Push(DoorWindowElevationGeometryBuilder.SerializeCellLayout(Cells));
            Cells=DoorWindowElevationGeometryBuilder.ParseCellLayout(source.Pop());return true;
        }
        public bool Split(IEnumerable<int> indices,bool vertical,int count,out string error)
        {
            var selected=new HashSet<int>(indices);count=Math.Max(2,Math.Min(20,count));
            return Edit(cells=> {
                var extra=new List<DoorWindowLayoutCell>();
                foreach(var index in selected.OrderByDescending(i=>i)) {
                    var c=cells[index];if(c.IsDeleted)continue;cells.RemoveAt(index);
                    for(var i=0;i<count;i++)extra.Add(new DoorWindowLayoutCell {
                        Left=vertical ? c.Left+(c.Right-c.Left)*i/count : c.Left,
                        Right=vertical ? c.Left+(c.Right-c.Left)*(i+1)/count : c.Right,
                        Bottom=vertical ? c.Bottom : c.Bottom+(c.Top-c.Bottom)*i/count,
                        Top=vertical ? c.Top : c.Bottom+(c.Top-c.Bottom)*(i+1)/count,
                        IsDoor=i==0 && c.IsDoor,Material=c.Material,Opening=c.Opening });
                }cells.AddRange(extra);
            },out error);
        }
        public bool Merge(IEnumerable<int> indices,out string error)
        {
            var selected=indices.Distinct().ToArray();
            return Edit(cells=> {
                if(selected.Length<2)throw new InvalidOperationException("请选择相邻面板后合并。");
                var group=selected.Select(i=>cells[i]).ToArray();
                var l=group.Min(c=>c.Left);var r=group.Max(c=>c.Right);var b=group.Min(c=>c.Bottom);var t=group.Max(c=>c.Top);
                if(group.Any(c=>c.IsDeleted) || Math.Abs((r-l)*(t-b)-group.Sum(c=>(c.Right-c.Left)*(c.Top-c.Bottom)))>.1)
                    throw new InvalidOperationException("只能合并能够组成完整矩形的相邻面板。");
                var first=group[0];foreach(var i in selected.OrderByDescending(i=>i))cells.RemoveAt(i);
                cells.Add(new DoorWindowLayoutCell {Left=l,Right=r,Bottom=b,Top=t,Opening=string.IsNullOrWhiteSpace(first.Opening) ? group.Select(c=>c.Opening).FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x)) : first.Opening,
                    Material=string.IsNullOrWhiteSpace(first.Material) ? group.Select(c=>c.Material).FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x)) : first.Material,IsDoor=group.Any(c=>c.IsDoor)});
            },out error);
        }
        public bool SetCellSize(int index,double width,double height,out string error)
        {
            error=null;if(index<0 || index>=Cells.Count || width<1 || height<1){error="请选择一格并输入有效尺寸。";return false;}
            var cell=Cells[index];var trial=new OpeningLayoutEditing(Width,Height,Cells);
            if(Math.Abs(width-(cell.Right-cell.Left))>.001 && !trial.MoveDivider(true,cell.Right,cell.Left+width,out error,(cell.Bottom+cell.Top)/2))return false;
            cell=trial.Cells[index];
            if(Math.Abs(height-(cell.Top-cell.Bottom))>.001 && !trial.MoveDivider(false,cell.Bottom,cell.Top-height,out error,(cell.Left+cell.Right)/2))return false;
            return Edit(cells=>{cells.Clear();cells.AddRange(trial.Cells);},out error);
        }
        public bool Equalize(IEnumerable<int> indices,bool widths,out string error)
        {
            var selected=indices.Distinct().ToArray();
            return Edit(cells=>{
                if(selected.Length<2)throw new InvalidOperationException("请选择连续的多个面板。");
                var groups=widths ? selected.Select(i=>cells[i]).GroupBy(c=>c.Bottom.ToString("R")+":"+c.Top.ToString("R")) : selected.Select(i=>cells[i]).GroupBy(c=>c.Left.ToString("R")+":"+c.Right.ToString("R"));
                var changed=false;
                foreach(var group in groups.Where(g=>g.Count()>1)){
                    var ordered=(widths ? group.OrderBy(c=>c.Left) : group.OrderBy(c=>c.Bottom)).ToArray();
                    if(ordered.Any(c=>c.IsDeleted) || ordered.Skip(1).Where((c,i)=>Math.Abs((widths ? ordered[i].Right-c.Left : ordered[i].Top-c.Bottom))>.05).Any())continue;
                    var a=widths ? ordered[0].Left : ordered[0].Bottom;var b=widths ? ordered.Last().Right : ordered.Last().Top;
                    for(var i=0;i<ordered.Length;i++)if(widths){ordered[i].Left=a+(b-a)*i/ordered.Length;ordered[i].Right=a+(b-a)*(i+1)/ordered.Length;}
                        else{ordered[i].Bottom=a+(b-a)*i/ordered.Length;ordered[i].Top=a+(b-a)*(i+1)/ordered.Length;}
                    changed=true;
                }
                if(!changed)throw new InvalidOperationException("所选面板须在同一行或列连续排列。");
            },out error);
        }
        public bool Center(IEnumerable<int> indices,out string error)
        {
            var selected=new HashSet<int>(indices);
            return Edit(cells=>{
                var group=selected.Select(i=>cells[i]).OrderBy(c=>c.Left).ToArray();
                if(group.Length==0 || group.Any(c=>c.IsDeleted || Math.Abs(c.Bottom-group[0].Bottom)>.05 || Math.Abs(c.Top-group[0].Top)>.05)
                    || group.Skip(1).Where((c,i)=>Math.Abs(group[i].Right-c.Left)>.05).Any())throw new InvalidOperationException("请选择同一行的连续面板。");
                var left=group.First().Left;var right=group.Last().Right;var side=(Width-(right-left))/2;
                if(side<50 || left<.05 || right>Width-.05)throw new InvalidOperationException("居中须在两侧保留可调整的邻格，至少 50 mm。");
                foreach(var pair in cells.Select((c,i)=>new{c,i}).Where(p=>!selected.Contains(p.i) && Math.Abs(p.c.Bottom-group[0].Bottom)<.05 && Math.Abs(p.c.Top-group[0].Top)<.05)){
                    var c=pair.c;
                    if(c.Right<=left+.05){c.Left=c.Left/left*side;c.Right=c.Right/left*side;}
                    else if(c.Left>=right-.05){c.Left=side+right-left+(c.Left-right)/(Width-right)*side;c.Right=side+right-left+(c.Right-right)/(Width-right)*side;}
                }
                foreach(var c in group){c.Left+=side-left;c.Right+=side-left;}
            },out error);
        }
        public bool MoveDivider(bool vertical,double coordinate,double target,out string error,double? along=null,double snapStep=0)
        {
            error=null;
            if(!Finite(coordinate)||!Finite(target)||coordinate<=.05 || coordinate>=(vertical ? Width : Height)-.05){error="外边框已固定，请调整内部中梃。";return false;}
            var segments=Cells.Where(c=>vertical ? Math.Abs(c.Left-coordinate)<.05 || Math.Abs(c.Right-coordinate)<.05 : Math.Abs(c.Bottom-coordinate)<.05 || Math.Abs(c.Top-coordinate)<.05)
                .Select(c=>Tuple.Create(vertical ? c.Bottom : c.Left,vertical ? c.Top : c.Right)).ToList();
            var seed=segments.FirstOrDefault(s=>!along.HasValue || along.Value>=s.Item1-.05 && along.Value<=s.Item2+.05);
            if(seed==null){error="此处没有可调整的内部中梃。";return false;}
            var start=seed.Item1;var end=seed.Item2;var changed=true;
            while(changed){changed=false;foreach(var s in segments)if(s.Item2>=start-.05 && s.Item1<=end+.05){var a=Math.Min(start,s.Item1);var b=Math.Max(end,s.Item2);if(a!=start||b!=end){start=a;end=b;changed=true;}}}
            var connected=Cells.Where(c=>vertical ? c.Top>start+.05 && c.Bottom<end-.05 : c.Right>start+.05 && c.Left<end-.05).ToArray();
            var before=connected.Where(c=>Math.Abs((vertical ? c.Right : c.Top)-coordinate)<.05).ToArray();
            var after=connected.Where(c=>Math.Abs((vertical ? c.Left : c.Bottom)-coordinate)<.05).ToArray();
            if(before.Length==0 || after.Length==0){error="外边框或开口边缘不能作为内部中梃移动。";return false;}
            var lower=before.Max(c=>vertical ? c.Left : c.Bottom)+50;
            var upper=after.Min(c=>vertical ? c.Right : c.Top)-50;
            if(lower>upper){error="相邻分格须至少保留 50 mm。";return false;}
            if(snapStep>0){var snappedLower=Math.Ceiling(lower/snapStep)*snapStep;var snappedUpper=Math.Floor(upper/snapStep)*snapStep;
                target=Math.Round(target/snapStep,MidpointRounding.AwayFromZero)*snapStep;
                if(snappedLower<=snappedUpper){lower=snappedLower;upper=snappedUpper;}}
            target=Math.Max(lower,Math.Min(upper,target));
            LastDividerCoordinate=target;
            return Edit(cells=> {
                foreach(var c in cells.Where(c=>vertical ? c.Top>start+.05 && c.Bottom<end-.05 : c.Right>start+.05 && c.Left<end-.05))if(vertical) {
                    if(Math.Abs(c.Left-coordinate)<.05)c.Left=target;
                    if(Math.Abs(c.Right-coordinate)<.05)c.Right=target;
                }else {
                    if(Math.Abs(c.Bottom-coordinate)<.05)c.Bottom=target;
                    if(Math.Abs(c.Top-coordinate)<.05)c.Top=target;
                }
            },out error);
        }
        private static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
    }
}
