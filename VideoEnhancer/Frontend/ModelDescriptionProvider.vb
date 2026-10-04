Imports System
Imports System.Collections.Generic
Imports System.Linq

Namespace videoenhancer
    Friend NotInheritable Class ModelDescriptionProvider
        Friend Shared Function FallbackArchitecture(modelId As String) As String
            Dim normalized = If(modelId, "").Replace(Convert.ToChar(92), "/"c)
            For Each architecture In New String() {
                "RealESRGAN", "RealHatGAN", "ESRGAN", "SPANPlus", "SPAN", "SwinIR", "RealCUGAN",
                "AnimeSR", "CRAFT", "DITN", "MoSR", "RIFE", "GMFSS", "GIMM"
            }
                If normalized.IndexOf(architecture, StringComparison.OrdinalIgnoreCase) >= 0 Then Return architecture
            Next
            Dim segments = normalized.Split(New Char() {"/"c}, StringSplitOptions.RemoveEmptyEntries)
            Return If(segments.Length > 1, segments(0), "其他模型")
        End Function

        ' 文案依据与待复核项见 docs/model-introduction-sources.md；架构不用于推断训练题材。
        Friend Shared Function ModelIntroduction(entry As ModelCatalogItem,
                                                   interpolation As Boolean) As String
            If entry Is Nothing Then Return ""
            If interpolation Then Return InterpolationModelIntroduction(entry)
            Dim key = (If(entry.Id, "") & " " & If(entry.DisplayName, "")).ToLowerInvariant()
            If key.Contains("basicvsr") Then Return "连续视频复原，利用前后帧信息恢复细节；适合低清实拍视频。本插件不支持与补帧组合。"
            If key.Contains("flashvsr") Then Return "单步扩散式视频超分，利用时序信息重建细节；支持直接 2x/4x。重建纹理可能偏离原片，先检查人物与文字。"
            If key.Contains("animejanai") Then
                Dim description = If(key.Contains("-sd-"),
                    "面向标清动画的修复模型，用于低清线条与缩放模糊；当前为 beta34，HD 原片优先比较 HD 系列。",
                    "面向高清动画，修复制作分辨率放大带来的模糊和锯齿，兼顾线条粗细与景深保留。")
                If key.Contains("v3.1") Then
                    description &= If(key.Contains("performance"), " Performance 优先速度。", " Balanced 兼顾速度与细节。")
                ElseIf key.Contains("v2") Then
                    description &= " V2 是早期版本；V3 更注重振铃、线色与柔和阴影处理。"
                End If
                If key.Contains("sharp1") Then description &= " Sharp1 额外强调清晰度，注意高对比边缘是否过锐。"
                Return description
            End If
            If key.Contains("anisd") Then Return AniSdModelIntroduction(key, entry.Architecture)
            If key.Contains("aniscale2") Then
                If key.Contains("refiner") Then Return "动画线稿修整与轻度锐化；放大前可修线，放大后可细化线条。可能削弱浅景深，原生 1x。"
                Dim description = "面向约 2000 年后的动画，清理 WEB/DVD 压缩并保留景深。"
                If key.Contains("omni") Then Return description & " OmniSR 是作者推荐的综合起点。"
                If key.Contains("ditn") Then Return description & " 作者不推荐该 DITN 版本：模糊/景深和细节保留较弱。"
                If key.Contains("swinir") Then Return description & " SwinIR 较慢，部分素材更忠实，可与 OmniSR 对照。"
                If key.Contains("lite") Then Return description & " ESRGAN Lite 为缩小网络，优先速度；虚化区域建议与 OmniSR 比较。"
                Return description & " ESRGAN 版本；重点对照模糊区域与细节保留。"
            End If
            If key.Contains("anitoon") Then
                Dim description = "修复 1990/2000 年代低质量卡通与动画，训练覆盖多种老片来源。"
                If key.Contains("rplksrs") Then description &= " Small 为作者建议的起点。"
                If key.Contains("rplksrl") Then description &= " Large 是更大网络，适合与 Small 比较修复效果。"
                Return description & " 作者曾报告 VSMLRT 输出偏差，TensorRT 结果宜与 CUDA 对照。"
            End If
            If key.Contains("openproteus") Then Return "面向高质量、低噪声的 HD/FHD 实拍影视，侧重忠实放大并避免过锐；重噪声、低清老片不是其主要目标。"
            If key.Contains("ani4k") Then Return "面向 720p/1080p 动画，侧重细节保留与自然的高清放大；重度 DVD 压缩更适合先比较 AniSD。"
            If key.Contains("nomos8k") Then
                Dim strength = If(key.Contains("strong"), "strong：针对较重退化。", If(key.Contains("weak"), "weak：针对较轻退化。", "medium：中等退化。"))
                Return "以照片为主要用途的 OTF 退化修复模型。" & strength & " 按压缩/噪声程度选择，检查肤质与细小纹理是否被改写。"
            End If
            If key.Contains("animevideov3") Then Return "轻量动画视频模型，侧重线条与压缩画面恢复；逐帧处理，不利用相邻帧。PTH 原生 4x，NCNN 的 2x/3x/4x 为独立模型。"
            If key.Contains("general-wdn-x4v3") Then Return "通用实拍模型的弱去噪权重，用于与 General x4v3 做去噪强度插值；单独使用偏保留噪声与纹理。"
            If key.Contains("general-x4v3") Then Return "轻量通用实拍修复，处理压缩、模糊与噪声；相较完整 RRDB 网络更侧重速度。WDN 是配套弱去噪权重。"
            If key.Contains("x4plus_anime") OrElse key.Contains("x4plus-anime") Then Return "动漫与插画专用的 6 块 RRDB 网络；比通用 x4plus 网络更小。检查细线、景深与边缘是否被过度修复。"
            If key.Contains("realesrgan_x2plus") OrElse key.Contains("realesrgan_x4plus") Then Return "通用真实图像 GAN 修复，覆盖压缩、模糊和噪声；可重建纹理，但人脸、小字与细纹可能出现生成痕迹。"
            If key.Contains("realhatgan") Then Return "插画修复导出模型，实际网络为 HAT；JP 与 Universal 表示不同权重。原生 1x 仅修复；fix 编号不代表画质等级，训练差异待复核。"
            If key.Contains("jp-illustration") Then Return "日式插画导出模型；适合线稿与平涂素材试用。fix1/fix2 的训练与画质差异暂无可核实说明，不据编号判断优劣。"
            If key.Contains("waifu2x") Then
                If key.Contains("photo") Then Return "照片训练版本，用于实拍图像的去噪与放大；动画线稿优先比较动漫训练版本。"
                If key.Contains("noise3") Then Return "动漫/插画放大并强去噪；适合较重噪声，注意细线与纹理损失。"
                If key.Contains("noise2") Then Return "动漫/插画放大并中等去噪；用于明显噪声，可与 Noise1 对照细节保留。"
                If key.Contains("noise1") Then Return "动漫/插画放大并轻度去噪；用于轻微噪声，优先保留线条与纹理。"
                If key.Contains("noise0") Then Return "动漫/插画的 Noise0 版本；零级去噪不等于完全不改画面，干净素材应与无去噪版本比较。"
                Return "动漫/插画基础放大模型；先按噪声程度选择去噪版本，干净原片避免强去噪。"
            End If
            If key.Contains("cugan-conservative") Then Return "动漫保守修复，减少纹理、色彩和画风改变；适合较干净素材，严重模糊/压缩时修复力度有限。"
            If key.Contains("denoiseh264") Then Return "针对 H.264 压缩伪影的清理模型，原生 1x；适合块状压缩和边缘杂点，不能替代所有类型的降噪。"
            If key.Contains("dncnn") Then Return "彩色盲高斯去噪，不必指定噪声强度；原生 1x。真实压缩块与复杂混合噪声不属于其主要训练任务。"
            If key.Contains("animesr") Then Return "动画视频时序复原，利用邻帧信息改善连续性；适合动画视频。与逐帧 AnimeVideo v3 不同，当前仅 CUDA。"
            If key.Contains("apisr") Then Return "按动画制作退化训练，重点恢复手绘线条并控制颜色伪影；RRDB/DAT/GRL 是不同网络，不是画质档位。GAN 重建宜检查小字与纹理。"
            If key.Contains("modernspanimation") Then Return "ModernSpanimation 动画放大系列；V2 与 V3 是不同训练版本，当前结构分别为 SPAN 与 SPANPlus。尚无足够作者资料确认画质差异，建议同帧比较。"
            If key.Contains("bhi-spanplus") OrElse key.Contains("sudo-shuffle") Then Return "SPAN 家族的导出/训练变体；具体训练题材与去噪强度待复核，不据架构名称推断画风或画质。"
            Return "未核实此权重的训练题材与修复强度；架构、原生倍率及可用后端见下方，建议先用短片或单帧比较。"
        End Function

        Friend Shared Function InterpolationModelIntroduction(entry As ModelCatalogItem) As String
            Dim key = (If(entry.Id, "") & " " & If(entry.DisplayName, "")).ToLowerInvariant()
            If key.Contains("rife") Then
                Dim description = "根据相邻两帧估计中间帧，适合通用视频补帧；遮挡、细线和镜头切换处需检查重影。"
                If key.Contains("lite") Then description &= " Lite 为轻量版本；速度与效果按素材比较。"
                If key.Contains("heavy") Then description &= " Heavy 为较大网络；不保证所有运动都优于普通版。"
                Return description
            End If
            If key.Contains("gmfss") Then Return "融合运动、特征与 softsplat 的补帧系列；AnimeRun 对应动画训练，Base/Union 为不同配置。检查快速运动与遮挡，不按版本名判断质量。"
            If key.Contains("gimm") Then
                Dim description = "通过隐式运动表示生成中间时刻画面；"
                description &= If(key.Contains("-f"), "F 使用 FlowFormer 光流。", "R 使用 RAFT 光流。")
                If key.Contains("lpips") Then description &= " LPIPS 版增加感知损失训练，侧重视觉观感。"
                Return description & " 检查遮挡与运动边缘。"
            End If
            Return "根据相邻帧生成中间帧；该权重的训练题材未核实，建议先检查快速运动与镜头切换。"
        End Function

        Friend Shared Function AniSdModelIntroduction(key As String, architecture As String) As String
            Dim description As String
            If key.Contains("db-i2") Then
                Return "标清动画去色渗模型，原生 1x；用于线条附近色彩溢出，建议在放大前处理。不是通用去噪，对彩虹纹改善有限。"
            ElseIf key.Contains("ps-g6i2") Then
                Return "标清动画纯放大版本，侧重保持原片外观；适合不需要强修复的素材，不用于重度压缩清理。"
            ElseIf key.Contains("-dc-") Then
                description = "标清动画深度清理，针对老 DVD、重压缩与严重模糊；基础版/AC 无法修复时再比较。"
            ElseIf key.Contains("-ac-") Then
                description = "标清动画增强清理，处理较重压缩、噪声、光晕与点爬；适合退化明显的 DVD/WEB 源。"
            Else
                description = "标清动画基础修复，面向较干净的 DVD/WEB 源；清理轻度压缩、噪声、光晕和色彩问题。"
            End If
            Select Case If(architecture, "").ToUpperInvariant()
                Case "DAT", "DAT2"
                    description &= " DAT 版本开销大，注意颜色偏移。"
                Case "SPAN", "REALPLKSR", "COMPACT", "REALESRGAN COMPACT"
                    description &= " " & If(architecture, "") & " 版本。"
                Case "SWINIR", "CRAFT"
                    description &= " " & architecture & " 版本，处理较慢。"
            End Select
            Return description
        End Function

        Friend Shared Function ModelTooltipText(entry As ModelCatalogItem,
                                                  interpolation As Boolean) As String
            If entry Is Nothing Then Return ""
            Dim lines As New List(Of String)()
            Dim source = If(String.Equals(entry.Source, "user", StringComparison.OrdinalIgnoreCase), "用户导入", "")
            lines.Add(If(entry.DisplayName, "") & If(source.Length > 0, " · " & source, ""))
            lines.Add(ModelIntroduction(entry, interpolation))
            Dim details As New List(Of String)()
            If Not String.IsNullOrWhiteSpace(entry.Architecture) Then details.Add("架构：" & entry.Architecture)
            If Not interpolation AndAlso entry.Scale > 0 Then details.Add("原生 " & entry.Scale.ToString() & "x")
            If details.Count > 0 Then lines.Add(String.Join("；", details))
            If entry.Backends IsNot Nothing AndAlso entry.Backends.Length > 0 Then
                lines.Add("支持后端：" & String.Join(" / ", entry.Backends.Select(Function(value) BackendDisplayName(value))))
            End If
            Return String.Join(Environment.NewLine, lines.Where(Function(line) Not String.IsNullOrWhiteSpace(line)))
        End Function

        Friend Shared Function BackendDisplayName(value As String) As String
            Select Case If(value, "").Trim().ToLowerInvariant()
                Case "ncnn"
                    Return "NCNN"
                Case "cuda"
                    Return "CUDA"
                Case "tensorrt"
                    Return "TensorRT"
                Case "onnx"
                    Return "ONNX"
                Case "flashvsr"
                    Return "FlashVSR"
                Case "basicvsrpp"
                    Return "BasicVSR++"
                Case Else
                    Return If(value, "").Trim()
            End Select
        End Function

    End Class
End Namespace
