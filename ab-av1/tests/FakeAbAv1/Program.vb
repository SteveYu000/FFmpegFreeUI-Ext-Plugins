Imports System.Threading
Imports System.Globalization

Module Program

    Sub Main(args As String())
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

End Module
