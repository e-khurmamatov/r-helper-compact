using System;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace RHelper.Compact;

internal sealed record ReleaseVersion(int Major,int Minor,int Patch,string Preview) : IComparable<ReleaseVersion>
{
    public Version Numeric => new(Major,Minor,Patch);
    public static ReleaseVersion? Parse(string text) {
        var m=Regex.Match(text,@"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$");
        if(!m.Success || !int.TryParse(m.Groups[1].Value,out int a)||!int.TryParse(m.Groups[2].Value,out int b)||!int.TryParse(m.Groups[3].Value,out int c))return null;
        string pre=m.Groups[4].Value;
        if(pre.Split('.').Any(x=>x.Length>1&&x[0]=='0'&&x.All(char.IsAsciiDigit)))return null;
        return new(a,b,c,pre);
    }
    public int CompareTo(ReleaseVersion? other) {
        if(other is null)return 1;
        int n=Numeric.CompareTo(other.Numeric);if(n!=0)return n;
        if(Preview==other.Preview)return 0;
        if(Preview.Length==0)return 1;if(other.Preview.Length==0)return -1;
        var left=Preview.Split('.');var right=other.Preview.Split('.');
        for(int i=0;i<Math.Min(left.Length,right.Length);i++) {
            bool a=left[i].All(char.IsAsciiDigit),b=right[i].All(char.IsAsciiDigit);
            n=a&&b?BigInteger.Parse(left[i]).CompareTo(BigInteger.Parse(right[i])):a?-1:b?1:string.CompareOrdinal(left[i],right[i]);if(n!=0)return n;
        }
        return left.Length.CompareTo(right.Length);
    }
}
