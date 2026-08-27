Imports System.Threading

Module Program

    Sub Main(args As String())
        If args.Length = 0 OrElse Not String.Equals(args(0), "crf-search", StringComparison.Ordinal) Then
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

        Console.WriteLine("{""type"":""sample-encode-done"",""crf"":30,""vmaf"":94.7}")
        Console.WriteLine(
            "{""type"":""crf-search-done"",""crf"":28,""vmaf"":95.2," &
            """predicted_encode_size"":123456789,""predicted_encode_seconds"":321.5}")
    End Sub

End Module
