Imports System
Imports System.Drawing
Imports LakeUI
Imports Vortice.Direct2D1

Namespace videoenhancer
    ' 预览帧由插件解码和持有，LakeUI 只通过公开图像源接口呈现。
    Friend NotInheritable Class PreviewPictureSource
        Private ReadOnly _image As Image
        Private ReadOnly _source As PixelPictureCallbackSource

        Private Sub New(image As Image)
            _image = image
            _source = New PixelPictureCallbackSource(image.Width, image.Height,
                AddressOf Present, AddressOf ReadPixel, Nothing,
                colorSpace:=PixelPictureColorSpace.SRgb,
                transferFunction:=PixelPictureTransferFunction.SRgb,
                pixelFormat:=PixelPicturePixelFormat.Bgra32)
        End Sub

        Friend Shared Function Create(image As Image) As IPixelPictureSource
            If image Is Nothing Then Return Nothing
            Return New PreviewPictureSource(image)._source
        End Function

        Friend Shared Sub Release(image As Image)
            If image Is Nothing Then Return
            ' 图像源可能正在 GPU 绘制线程读取；释放前与完整绘制过程串行化。
            SyncLock image
                image.Dispose()
            End SyncLock
        End Sub

        Private Function Present(context As D3D_PaintContext, destination As RectangleF,
                                 region As PixelPictureRegion, frameIndex As Integer,
                                 interpolation As InterpolationMode) As Boolean
            SyncLock _image
                Try
                    context.DrawImage(_image, destination, region.ToRectangleF(), interpolation:=interpolation)
                    Return True
                Catch ex As ObjectDisposedException
                    Return False
                Catch ex As ArgumentException
                    Return False
                End Try
            End SyncLock
        End Function

        Private Function ReadPixel(x As Long, y As Long, frameIndex As Integer,
                                   ByRef pixel As PixelPicturePixel) As Boolean
            SyncLock _image
                Try
                    Dim bitmap = TryCast(_image, Bitmap)
                    If bitmap Is Nothing OrElse x < 0 OrElse y < 0 OrElse
                        x >= bitmap.Width OrElse y >= bitmap.Height Then Return False
                    Dim color = bitmap.GetPixel(CInt(x), CInt(y))
                    pixel = New PixelPicturePixel(x, y, color.R / 255.0F, color.G / 255.0F,
                        color.B / 255.0F, color.A / 255.0F, PixelPictureColorSpace.SRgb,
                        PixelPictureTransferFunction.SRgb, PixelPicturePixelFormat.Bgra32, False)
                    Return True
                Catch ex As ObjectDisposedException
                    Return False
                Catch ex As ArgumentException
                    Return False
                End Try
            End SyncLock
        End Function
    End Class
End Namespace
