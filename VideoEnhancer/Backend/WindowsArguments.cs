using System.Text;
namespace VideoEnhancer;

// 为实际的 FFmpeg、Python 和 RTX 子进程处理 Windows 参数转义。
public static class WindowsArguments
{
    public static IReadOnlyList<string> Parse(string value)
    {
        var result=new List<string>();var token=new StringBuilder();bool quoted=false,active=false;
        for(int i=0;i<value.Length;i++)
        {
            char c=value[i];
            if(c=='\\')
            {
                int start=i;while(i<value.Length&&value[i]=='\\')i++;
                int slash=i-start;
                if(i<value.Length&&value[i]=='"'){token.Append('\\',slash/2);if(slash%2==1)token.Append('"');else quoted=!quoted;active=true;}
                else {token.Append('\\',slash);i--;}
            }
            else if(c=='"'){quoted=!quoted;active=true;}
            else if(char.IsWhiteSpace(c)&&!quoted){if(active){result.Add(token.ToString());token.Clear();active=false;}}
            else {token.Append(c);active=true;}
        }
        if(quoted)throw new FormatException("命令参数引号未闭合");
        if(active)result.Add(token.ToString());return result;
    }
    public static string Quote(string value)
    {
        if(value.Length>0&&!value.Any(c=>char.IsWhiteSpace(c)||c=='"'))return value;
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in value){if(c=='\\'){slashes++;continue;}if(c=='"')result.Append('\\',slashes*2+1).Append('"');else result.Append('\\',slashes).Append(c);slashes=0;}
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    public static string Join(IEnumerable<string> values)=>string.Join(" ",values.Select(Quote));
}
