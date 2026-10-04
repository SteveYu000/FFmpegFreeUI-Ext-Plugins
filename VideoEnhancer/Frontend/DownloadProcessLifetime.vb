Imports System
Imports System.ComponentModel
Imports System.Diagnostics
Imports Process = VideoEnhancer.BackendOperation
Imports System.Runtime.InteropServices

Namespace videoenhancer
    ' 下载进程与宿主共用作业对象；宿主退出或页面销毁时，下载器及其子进程一起结束。
    Friend NotInheritable Class DownloadProcessLifetime
        Implements IDisposable

        Private Const JobObjectExtendedLimitInformation As Integer = 9
        Private Const KillOnJobClose As UInteger = &H2000UI
        Private ReadOnly _gate As New Object()
        Private _job As IntPtr
        Private _disposed As Boolean

        <StructLayout(LayoutKind.Sequential)>
        Private Structure BasicLimitInformation
            Public PerProcessUserTimeLimit As Long
            Public PerJobUserTimeLimit As Long
            Public LimitFlags As UInteger
            Public MinimumWorkingSetSize As UIntPtr
            Public MaximumWorkingSetSize As UIntPtr
            Public ActiveProcessLimit As UInteger
            Public Affinity As UIntPtr
            Public PriorityClass As UInteger
            Public SchedulingClass As UInteger
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Private Structure IoCounters
            Public ReadOperationCount As ULong
            Public WriteOperationCount As ULong
            Public OtherOperationCount As ULong
            Public ReadTransferCount As ULong
            Public WriteTransferCount As ULong
            Public OtherTransferCount As ULong
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Private Structure ExtendedLimitInformation
            Public BasicLimitInformation As BasicLimitInformation
            Public IoInfo As IoCounters
            Public ProcessMemoryLimit As UIntPtr
            Public JobMemoryLimit As UIntPtr
            Public PeakProcessMemoryUsed As UIntPtr
            Public PeakJobMemoryUsed As UIntPtr
        End Structure

        <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
        Private Shared Function CreateJobObject(attributes As IntPtr, name As String) As IntPtr
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function SetInformationJobObject(job As IntPtr, infoClass As Integer,
                                                        ByRef info As ExtendedLimitInformation, size As UInteger) As Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function AssignProcessToJobObject(job As IntPtr, process As IntPtr) As Boolean
        End Function

        <DllImport("kernel32.dll", SetLastError:=True)>
        Private Shared Function CloseHandle(handle As IntPtr) As Boolean
        End Function

        Friend Sub Register(process As Process)
            If process.IsManaged Then Return
            SyncLock _gate
                If _disposed Then Throw New ObjectDisposedException(NameOf(DownloadProcessLifetime))
                If _job = IntPtr.Zero Then
                    Dim job = CreateJobObject(IntPtr.Zero, Nothing)
                    If job = IntPtr.Zero Then Throw New Win32Exception(Marshal.GetLastWin32Error())
                    Dim info As New ExtendedLimitInformation()
                    info.BasicLimitInformation.LimitFlags = KillOnJobClose
                    If Not SetInformationJobObject(job, JobObjectExtendedLimitInformation, info,
                                                   CUInt(Marshal.SizeOf(Of ExtendedLimitInformation)())) Then
                        Dim errorCode = Marshal.GetLastWin32Error()
                        CloseHandle(job)
                        Throw New Win32Exception(errorCode)
                    End If
                    _job = job
                End If
                If Not AssignProcessToJobObject(_job, process.Handle) Then
                    Throw New Win32Exception(Marshal.GetLastWin32Error())
                End If
            End SyncLock
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            SyncLock _gate
                If _disposed Then Return
                _disposed = True
                If _job <> IntPtr.Zero Then
                    CloseHandle(_job)
                    _job = IntPtr.Zero
                End If
            End SyncLock
        End Sub
    End Class
End Namespace
