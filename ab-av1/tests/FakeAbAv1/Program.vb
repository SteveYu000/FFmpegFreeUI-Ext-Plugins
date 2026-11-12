Imports System.Threading
Imports System.Globalization
Imports System.IO

Module Program

    Sub Main(args As String())
        If args.Contains("-show_entries", StringComparer.Ordinal) Then
            ProbeVideoStreams(args)
            Return
        End If
        If args.Length = 0 OrElse (args(0) <> "crf-search" AndAlso args(0) <> "sample-encode") Then
            Environment.ExitCode = 2
            Console.Error.WriteLine("expected crf-search")
            Return
        End If

        If args.Contains("--help", StringComparer.Ordinal) Then
            Console.WriteLine("fake ab-av1 help: --stdout-format json")
            Return
        End If

        If args.Contains("99.999", StringComparer.Ordinal) Then
            Thread.Sleep(TimeSpan.FromSeconds(30))
            Return
        End If

        If args(0) = "sample-encode" Then
            Dim durationIndex = Array.IndexOf(args, "--sample-duration")
            Dim scenario = If(durationIndex >= 0, args(durationIndex + 1), "")
            If scenario = "invalid-json" Then
                Console.WriteLine("{invalid json")
                Return
            End If
            If scenario = "nonzero" Then
                Console.Error.WriteLine("fake sample encoding failed")
                Environment.ExitCode = 7
                Return
            End If
            Dim crfIndex = Array.IndexOf(args, "--crf")
            Dim crf = Double.Parse(args(crfIndex + 1), CultureInfo.InvariantCulture)
            Dim metric = If(args.Contains("--xpsnr", StringComparer.Ordinal), "xpsnr", "vmaf")
            Dim score = If(metric = "xpsnr", "38.2", "95.2")
            Dim prefix = If(scenario = "legacy-json", "{", "{""type"":""sample-encode-done"",""crf"":" & crf.ToString(CultureInfo.InvariantCulture) & ",")
            If scenario <> "missing-score" Then prefix &= """" & metric & """:" & score & ","
            Console.WriteLine(prefix & """predicted_encode_size"":123456789,""predicted_encode_seconds"":321.5,""predicted_encode_percent"":42.1}")
            Return
        End If

        If args.Contains("--min-xpsnr", StringComparer.Ordinal) Then
            Console.WriteLine("{""type"":""sample-encode-done"",""crf"":30,""xpsnr"":37.9}")
            Console.WriteLine(
                "{""type"":""crf-search-done"",""crf"":28,""xpsnr"":38.2," &
                """predicted_encode_size"":123456789,""predicted_encode_seconds"":321.5}")
        Else
            Console.WriteLine("{""type"":""sample-encode-done"",""crf"":30,""vmaf"":94.7}")
            Console.WriteLine(
                "{""type"":""crf-search-done"",""crf"":28,""vmaf"":95.2," &
                """predicted_encode_size"":123456789,""predicted_encode_seconds"":321.5}")
        End If
    End Sub

    Private Sub ProbeVideoStreams(args As String())
        Dim selector = Array.IndexOf(args, "-select_streams")
        Dim entries = Array.IndexOf(args, "-show_entries")
        If selector < 0 OrElse args(selector + 1) <> "v" OrElse args(entries + 1) <> "stream=index" Then
            Console.Error.WriteLine("expected a video-only stream index query")
            Environment.ExitCode = 8
            Return
        End If
        Select Case Path.GetFileName(args.Last())
            Case "two-videos.bin"
                Console.WriteLine("{""streams"":[{""index"":0},{""index"":2}]}")
            Case "no-video.bin"
                Console.WriteLine("{""streams"":[]}")
            Case "invalid-probe.bin"
                Console.WriteLine("invalid JSON")
            Case "missing-streams.bin"
                Console.WriteLine("{}")
            Case "missing-index.bin"
                Console.WriteLine("{""streams"":[{}]}")
            Case "failed-probe.bin"
                Console.Error.WriteLine("fake input cannot be probed")
                Environment.ExitCode = 7
            Case "waiting-probe.bin"
                Thread.Sleep(TimeSpan.FromSeconds(30))
            Case Else
                ' 视频流的绝对索引不必为 0：前面可以有音频、字幕或其他流。
                Console.WriteLine("{""streams"":[{""index"":2}]}")
        End Select
    End Sub

End Module
