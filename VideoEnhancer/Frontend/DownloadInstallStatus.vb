Imports System
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions

Namespace videoenhancer
    Friend NotInheritable Class DownloadInstallStatus
        Friend Shared Function IsDownloadInstalled(relativePath As String, coreRoot As String, Optional remoteSha256 As String = "") As Boolean
            If String.IsNullOrWhiteSpace(relativePath) Then Return False
            Try
                Dim normalized = relativePath.Replace("\"c, "/"c).TrimStart("/"c)
                Dim slash = normalized.IndexOf("/"c)
                If slash <= 0 Then Return False
                Dim category = normalized.Substring(0, slash)
                Dim suffix = normalized.Substring(slash + 1).Replace("/"c, Path.DirectorySeparatorChar)
                Dim destinationRoot = If(category.Equals("Backend", StringComparison.OrdinalIgnoreCase),
                    Path.Combine(coreRoot, "python"),
                    If(category.Equals("Bin", StringComparison.OrdinalIgnoreCase),
                        Path.Combine(coreRoot, "bin"), Path.Combine(coreRoot, "models", category)))
                Dim downloaded = Path.Combine(destinationRoot, suffix)
                If File.Exists(downloaded & ".pending") OrElse File.Exists(downloaded & ".aria2") OrElse
                   File.Exists(downloaded & ".part") Then Return False
                Dim isArchive = String.Equals(Path.GetExtension(suffix), ".7z", StringComparison.OrdinalIgnoreCase) OrElse
                    String.Equals(Path.GetExtension(suffix), ".zip", StringComparison.OrdinalIgnoreCase)
                ' Bin 压缩包可能已下载但只解压了一部分，不能把归档文件存在当作安装完成。
                If category.Equals("Bin", StringComparison.OrdinalIgnoreCase) AndAlso isArchive Then
                    Dim coreFile = ComponentCoreFile(coreRoot, normalized)
                    If String.IsNullOrEmpty(coreFile) OrElse Not File.Exists(coreFile) Then Return False
                    Dim marker = ComponentArchiveMarkerPath(coreRoot, normalized)
                    If File.Exists(marker) AndAlso Not String.IsNullOrWhiteSpace(remoteSha256) Then
                        Return File.ReadAllText(marker, Encoding.UTF8).Trim().Equals(remoteSha256, StringComparison.OrdinalIgnoreCase)
                    End If
                    ' 有远端哈希时不能只凭日期认作最新，同日修订包也必须识别。
                    Return False
                End If
                If File.Exists(downloaded) Then Return True

                ' 压缩包下载后会自动解压；刷新时用解压后的核心文件判断，清理压缩包后仍能保持“已存在”。
                If Not isArchive Then Return False
                If category.Equals("Backend", StringComparison.OrdinalIgnoreCase) Then
                    Return File.Exists(Path.Combine(coreRoot, "python", "python", "python.exe"))
                End If
                If category.Equals("Frame-Interpolation", StringComparison.OrdinalIgnoreCase) Then
                    Return IsDownloadArchive(suffix) AndAlso
                        File.Exists(FrameInterpolationArchiveMarkerPath(coreRoot, normalized))
                End If
                If category.Equals("RIFE", StringComparison.OrdinalIgnoreCase) Then
                    Return Directory.Exists(Path.Combine(coreRoot, "models", "RIFE")) AndAlso
                        Directory.EnumerateFiles(Path.Combine(coreRoot, "models", "RIFE"), "*.param", SearchOption.AllDirectories).Any() AndAlso
                        Directory.EnumerateFiles(Path.Combine(coreRoot, "models", "RIFE"), "*.bin", SearchOption.AllDirectories).Any()
                End If
                If category.Equals("Param-Bin", StringComparison.OrdinalIgnoreCase) Then
                    Dim modelsRoot = Path.Combine(coreRoot, "models")
                    Return Directory.Exists(modelsRoot) AndAlso
                        Directory.EnumerateFiles(modelsRoot, "*.param", SearchOption.AllDirectories).Any() AndAlso
                        Directory.EnumerateFiles(modelsRoot, "*.bin", SearchOption.AllDirectories).Any()
                End If
                Return False
            Catch
                Return False
            End Try
        End Function

        Friend Shared Function IsDownloadArchive(valuePath As String) As Boolean
            Select Case Path.GetExtension(valuePath).ToLowerInvariant()
                Case ".7z", ".zip", ".gz", ".xz", ".zst", ".tar", ".bz2", ".tgz", ".txz", ".tbz2", ".tzst"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        Friend Shared Function IsRtxVideoRuntimeDownload(relativePath As String) As Boolean
            If String.IsNullOrWhiteSpace(relativePath) Then Return False
            Dim normalized = relativePath.Replace("\"c, "/"c).TrimStart("/"c)
            Return Regex.IsMatch(normalized,
                "^Bin/rtx-video/RTXVideoRuntime_\d{8}\.7z$",
                RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
        End Function

        Friend Shared Function ComponentCoreFile(coreRoot As String, relativePath As String) As String
            Dim normalized = relativePath.Replace("\"c, "/"c).TrimStart("/"c)
            If IsRtxVideoRuntimeDownload(normalized) Then
                Return Path.Combine(coreRoot, "bin", "rtx-video", "runtime", "vsr_backend.exe")
            End If
            Select Case normalized.ToUpperInvariant()
                Case "BIN/FFMPEG.7Z" : Return Path.Combine(coreRoot, "bin", "ffmpeg", "ffmpeg.exe")
                Case "BIN/MKVTOOLNIX.7Z" : Return Path.Combine(coreRoot, "bin", "mkvtoolnix", "mkvmerge.exe")
                Case "BIN/PORTABLEGIT.7Z" : Return Path.Combine(coreRoot, "bin", "PortableGit", "cmd", "git.exe")
                Case Else : Return ""
            End Select
        End Function

        Friend Shared Function ComponentArchiveMarkerPath(coreRoot As String, relativePath As String) As String
            Dim normalized = relativePath.Replace("\"c, "/"c).ToUpperInvariant()
            Dim hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            Return Path.Combine(coreRoot, "bin", ".downloads", hash & ".installed")
        End Function

        Friend Shared Function FrameInterpolationArchiveMarkerPath(coreRoot As String, relativePath As String) As String
            Dim normalized = relativePath.Replace("\"c, "/"c).ToUpperInvariant()
            Dim hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            Return Path.Combine(coreRoot, "models", "Frame-Interpolation", ".downloads", hash & ".installed")
        End Function

    End Class
End Namespace
