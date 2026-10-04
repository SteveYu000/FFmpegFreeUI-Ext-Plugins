using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VideoEnhancer;

// 只改变本插件选中的视频来源，保留其他插件、音频、字幕和附件的输入编号。
public static class NativeVideoBinder
{
    public const string Marker = "VE_AI_VIDEO";
    private const string SelectionKey = "流控制_将视频参数应用于指定流";
    private static readonly Regex Stream = new(@"^(?:0:)?v:(\d+)\??$", RegexOptions.IgnoreCase);
    private static readonly Regex Marked = new(@"^VE_AI_VIDEO:v:(\d+)\??$");

    public static int[] SelectedOrdinals(string preset)
    {
        var data = JsonNode.Parse(string.IsNullOrWhiteSpace(preset) ? "{}" : preset)!.AsObject();
        var selected = data[SelectionKey] as JsonArray;
        if (selected is null || selected.Count == 0) return [0];
        var result = new List<int>();
        foreach (var item in selected)
        foreach (var raw in (item?.GetValue<string>() ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Marked.Match(raw);
            if (!match.Success) match = Stream.Match(raw);
            if (match.Success) result.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            else if (int.TryParse(raw.TrimEnd('?'), out var index) && index >= 0) result.Add(index);
            else throw new InvalidOperationException("AI 增强的视频流选择应使用 0:v:序号（例如 0:v:0）；当前值：" + raw);
        }
        return result.Count == 0 ? [0] : result.Distinct().ToArray();
    }

    public static string MarkPreset(string preset)
    {
        var selected = SelectedOrdinals(preset);
        var data = JsonNode.Parse(preset)!.AsObject();
        data[SelectionKey] = new JsonArray(selected.Select(index => (JsonNode?)JsonValue.Create($"{Marker}:v:{index}")).ToArray());
        // 完整自写命令通过显式视频映射或滤镜输入绑定；不修改预设中的原始文本。
        return data.ToJsonString();
    }

    public static string Bind(string command, IReadOnlyDictionary<int, string> videos, int originalVideoCount,
        bool preview = false)
    {
        var args = WindowsArguments.Parse(command).ToList();
        var inputs = new Dictionary<int,int>();
        int inputIndex = 0;
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] != "-i") continue;
            foreach (var video in videos)
                if (SamePath(args[i+1], video.Value)) inputs[video.Key] = inputIndex;
            inputIndex++;
        }
        if (inputs.Count != videos.Count) throw new InvalidOperationException("宿主最终命令缺少 AI 中间视频输入，无法安全绑定视频流");

        bool markerUsed = args.Any(value => value.Contains(Marker, StringComparison.Ordinal));
        bool negativeMarker = args.Zip(args.Skip(1), (a,b) => (a,b)).Any(pair => pair.a == "-map" && pair.b.StartsWith("-" + Marker, StringComparison.Ordinal));
        var consumed = new HashSet<int>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "-map" && i + 1 < args.Count)
            {
                var map = args[++i];
                foreach (var binding in inputs)
                {
                    var token = $"{Marker}:v:{binding.Key}";
                    if (map.TrimEnd('?') == token)
                    {
                        args[i] = $"{binding.Value}:v:0" + (map.EndsWith('?') ? "?" : "");
                        consumed.Add(binding.Key);
                    }
                    else if (map.TrimEnd('?') == "-" + token)
                        args[i] = $"-0:v:{binding.Key}" + (map.EndsWith('?') ? "?" : "");
                    else if (!markerUsed && map.TrimEnd('?') == $"0:v:{binding.Key}")
                    {
                        args[i] = $"{binding.Value}:v:0" + (map.EndsWith('?') ? "?" : "");
                        consumed.Add(binding.Key);
                    }
                }
                if (map is "0:v?" or "0:v")
                {
                    // 没有滤镜时，保留其他视频的广义映射要按原始视频顺序展开。
                    // 有负向排除映射时，宿主已将增强结果放在滤镜输出中，原始广义映射保持不变。
                    if (!negativeMarker)
                    {
                        if (originalVideoCount <= 0 && !preview) throw new InvalidOperationException("无法探测原始视频流数量");
                        var expanded = new List<string>();
                        int count = originalVideoCount > 0 ? originalVideoCount : Math.Max(1, inputs.Keys.Max()+1);
                        for (int ordinal = 0; ordinal < count; ordinal++)
                        {
                            expanded.Add("-map");
                            if (inputs.TryGetValue(ordinal, out var enhanced))
                            {
                                expanded.Add($"{enhanced}:v:0?");
                                consumed.Add(ordinal);
                            }
                            else expanded.Add($"0:v:{ordinal}?");
                        }
                        args.RemoveRange(i-1, 2);
                        args.InsertRange(i-1, expanded);
                        i += expanded.Count-2;
                    }
                }
                continue;
            }
            if (args[i] is "-filter_complex" or "-lavfi" or "-vf" or "-filter:v")
            {
                if (++i >= args.Count) throw new InvalidOperationException("视频滤镜参数缺少表达式");
                foreach (var binding in inputs)
                {
                    var marked = $"[{Marker}:v:{binding.Key}]";
                    var original = $"[0:v:{binding.Key}]";
                    if (args[i].Contains(marked, StringComparison.Ordinal))
                    {
                        args[i] = args[i].Replace(marked,$"[{binding.Value}:v:0]",StringComparison.Ordinal);
                        consumed.Add(binding.Key);
                    }
                    else if (!markerUsed && args[i].Contains(original, StringComparison.Ordinal))
                    {
                        args[i] = args[i].Replace(original,$"[{binding.Value}:v:0]",StringComparison.Ordinal);
                        consumed.Add(binding.Key);
                    }
                    if (!markerUsed && binding.Key == 0 && args[i].Contains("[0:v]",StringComparison.Ordinal))
                    {
                        args[i] = args[i].Replace("[0:v]",$"[{binding.Value}:v:0]",StringComparison.Ordinal);
                        consumed.Add(binding.Key);
                    }
                }
            }
        }
        if (args.Any(value => value.Contains(Marker,StringComparison.Ordinal)))
            throw new InvalidOperationException("命令中存在无法解析的 AI 视频占位符");
        if (consumed.Count != videos.Count)
            throw new InvalidOperationException("完整自写命令或自定义滤镜没有明确引用所选的视频流；请使用 -map 0:v:序号 或 [0:v:序号] 输入");
        return WindowsArguments.Join(args);
    }

    // 将主输入的时间相关选项复制到增强输入；不复制硬件解码器和其他输入的选项。
    public static string CopyInputTiming(string command,string videoInput,double relativeStart,double formatStart=0,double formatDuration=0)
    {
        var args=WindowsArguments.Parse(command).ToList();
        int mainInput=args.FindIndex(value=>value=="-i"),addedInput=args.FindIndex(value=>SamePath(value,videoInput));
        if(mainInput<0||addedInput<=0)return command;
        string? Option(string name)
        {
            for(int i=mainInput-2;i>=0;i--)if(args[i]==name)return args[i+1];
            return null;
        }
        double Time(string name,double fallback=0)
        {
            string? value=Option(name);
            if(value is null)return fallback;
            string signed=value.Trim();
            double parsed=0;
            var parts=signed.TrimStart('-','+').Split(':');
            if(parts.Length>3)throw new InvalidOperationException("AI 时间绑定不支持该 "+name+" 值："+value);
            foreach(var part in parts)
            {
                if(!double.TryParse(part,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)||!double.IsFinite(number))
                    throw new InvalidOperationException("AI 时间绑定不支持该 "+name+" 值："+value);
                parsed=parsed*60+number;
            }
            return signed.StartsWith('-')?-parsed:parsed;
        }
        if(Option("-isync") is not null||Option("-seek_timestamp")=="1"||
            (Option("-itsscale") is not null&&Time("-itsscale",1)!=1))
            throw new InvalidOperationException("AI 增强暂不支持 -isync、绝对 seek_timestamp 或非 1 的 itsscale 输入时间轴");
        bool inputRate=Option("-r") is not null||Option("-framerate") is not null;
        double videoStart=inputRate?0:relativeStart;
        double seek=Time("-ss");
        if(Option("-sseof") is not null)
        {
            if(formatDuration<=0)throw new InvalidOperationException("无法获得原始总时长，不能绑定 -sseof");
            seek=Math.Max(0,formatDuration+Time("-sseof"));
        }
        double enhancedSeek=Math.Max(0,seek-videoStart);
        bool copyTs=args.Contains("-copyts"),startAtZero=args.Contains("-start_at_zero");
        if(copyTs&&(Math.Abs(videoStart)>0.000001||Math.Abs(formatStart)>0.000001||Math.Abs(Time("-itsoffset"))>0.000001))
            throw new InvalidOperationException("带起始时间偏移的输入暂不支持 AI 与 -copyts 同时使用；请使用宿主默认的时间戳归零方式");
        var options=new List<string>();
        string Value(double number)=>number.ToString("0.#########",CultureInfo.InvariantCulture);
        if(Option("-ss") is not null||Option("-sseof") is not null)options.AddRange(["-ss",Value(enhancedSeek)]);
        if(Option("-t") is not null)
            options.AddRange(["-t",Value(Time("-t"))]);
        else if(Option("-to") is not null)options.AddRange(["-to",Value(Math.Max(0,Time("-to")-seek+enhancedSeek))]);
        foreach(string name in new[]{"-r","-framerate"})
            if(Option(name) is string value)options.AddRange([name,value]);
        double offset=Time("-itsoffset")+(copyTs?videoStart+(!startAtZero&&!inputRate?formatStart:0):videoStart-seek+enhancedSeek);
        if(Math.Abs(offset)>0.000001)options.AddRange(["-itsoffset",Value(offset)]);
        args.InsertRange(addedInput-1,options);
        return WindowsArguments.Join(args);
    }
    public static string PreserveVideoMetadata(string command,string output,IEnumerable<int> selected,bool keepOther,IReadOnlyDictionary<int,VideoStreamInfo>? sourceInfo=null)
    {
        var args=WindowsArguments.Parse(command).ToList();
        int insert=args.FindLastIndex(value=>SamePath(value,output)||value.Equals("NUL",StringComparison.OrdinalIgnoreCase));
        if(insert<0)throw new InvalidOperationException("无法定位宿主输出目标，不能恢复视频流元数据");
        bool filtered=args.Any(value=>value.Contains("[vout",StringComparison.Ordinal)||value.Contains("[vkeep",StringComparison.Ordinal));
        int index=0;
        var metadata=new List<string>();
        foreach(int original in selected)
        {
            int target=keepOther&&!filtered?original:index;
            bool UserTag(string name)
            {
                for(int i=0;i+1<args.Count;i++)
                    if((args[i]==$"-metadata:s:v:{target}"||args[i]=="-metadata:s:v"||args[i]==$"-metadata:s:{target}")&&args[i+1].StartsWith(name+"=",StringComparison.OrdinalIgnoreCase))return true;
                return false;
            }
            // 显式流级 map_metadata 会关闭所有流的默认元数据复制，因此补写视频字段而保留宿主的映射。
            if(sourceInfo is not null&&sourceInfo.TryGetValue(original,out var source))
            {
                bool explicitMapping=args.Contains($"-map_metadata:s:v:{target}")||args.Contains("-map_metadata:s:v")||args.Contains("-map_metadata:s");
                if(!explicitMapping&&source.Tags is not null)
                    foreach(var tag in source.Tags)
                        if(!tag.Key.Equals("rotate",StringComparison.OrdinalIgnoreCase)&&!UserTag(tag.Key))
                            metadata.AddRange([$"-metadata:s:v:{target}",tag.Key+"="+tag.Value]);
                if(!args.Contains($"-disposition:v:{target}")&&!args.Contains("-disposition:v"))
                    metadata.AddRange([$"-disposition:v:{target}",source.Disposition.Length>0?source.Disposition:"0"]);
            }
            if(!UserTag("rotate"))metadata.AddRange([$"-metadata:s:v:{target}","rotate=0"]);
            index++;
        }
        args.InsertRange(insert,metadata);
        return WindowsArguments.Join(args);
    }

    private static bool SamePath(string a,string b) => a.Replace('/','\\').Equals(b.Replace('/','\\'),StringComparison.OrdinalIgnoreCase);
}
