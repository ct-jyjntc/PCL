Imports System.IO.Pipes
Imports System.Security.Cryptography
Imports System.Security.Principal

Public Module ModLinkClient
    Private ReadOnly RequestGate As New SemaphoreSlim(1, 1)
    Private ReadOnly HelperDirectory As String = IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Link")
    Private ReadOnly PipeName As String = GetPipeName()
    Private HelperProcess As Process

    Private Function GetPipeName() As String
        Using Identity = WindowsIdentity.GetCurrent(), Hash = SHA256.Create()
            Dim AccountDirectory = IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCL", "Link")
            Dim Text = IO.Path.GetFullPath(AccountDirectory).TrimEnd("\"c).ToLowerInvariant() & "|" & Identity.User.Value
            Return "PCL.Link." & BitConverter.ToString(Hash.ComputeHash(Encoding.UTF8.GetBytes(Text))).Replace("-", "").Substring(0, 24)
        End Using
    End Function

    Public Async Function LinkRequestAsync(Request As JObject, Optional StartHelper As Boolean = False, Optional Elevated As Boolean = False) As Task(Of JObject)
        Await RequestGate.WaitAsync().ConfigureAwait(False)
        Try
            Return Await Task.Run(
                Function() As JObject
                    Using Pipe As New NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous)
                        Try
                            Pipe.Connect(300)
                        Catch ex As TimeoutException
                            If Not StartHelper Then Throw New IOException("联机后台未启动，请点击登录或创建网络。")
                            StartLinkHelper(Elevated)
                            Try
                                Pipe.Connect(15000)
                            Catch timeout As TimeoutException
                                Throw New IOException("联机后台未连接。请确认使用同一个 Windows 账号完成管理员授权，并使用完整构建。", timeout)
                            End Try
                        End Try
                        Using Reader As New StreamReader(Pipe, Encoding.UTF8, False, 4096, True),
                              Writer As New StreamWriter(Pipe, New UTF8Encoding(False), 4096, True)
                            Writer.WriteLine(Request.ToString(Formatting.None))
                            Writer.Flush()
                            Dim Read = Reader.ReadLineAsync()
                            If Not Read.Wait(25000) Then Throw New TimeoutException("联机后台响应超时，请重试。")
                            Dim Line = Read.GetAwaiter().GetResult()
                            If Line Is Nothing OrElse Line.Length > 262144 Then Throw New IOException("联机后台返回了无效状态。")
                            Dim Reply = JObject.Parse(Line)
                            If Not CBool(Reply("ok")) Then Throw New IOException(CStr(Reply("error")))
                            Return Reply
                        End Using
                    End Using
                End Function).ConfigureAwait(False)
        Finally
            RequestGate.Release()
        End Try
    End Function

    Private Sub StartLinkHelper(Elevated As Boolean)
        Dim Exe = IO.Path.Combine(HelperDirectory, "PCL.Link.exe")
        If Not File.Exists(Exe) Then Throw New FileNotFoundException("缺少内置联机模块，请使用包含 Link 文件夹的完整构建。")
        If Not Environment.Is64BitOperatingSystem Then Throw New PlatformNotSupportedException("内置联机目前需要 Windows 10/11 x64。")
        Dim Info As New ProcessStartInfo(Exe) With {
            .UseShellExecute = True, .Verb = If(Elevated, "runas", ""), .WorkingDirectory = HelperDirectory,
            .WindowStyle = ProcessWindowStyle.Hidden}
        Try
            HelperProcess = Process.Start(Info)
        Catch ex As ComponentModel.Win32Exception When ex.NativeErrorCode = 1223
            Throw New IOException("已取消 Windows 授权；启动器仍可正常使用。")
        End Try
    End Sub

    Public Async Function LinkEnsureElevatedAsync() As Task
        Dim State = Await LinkRequestAsync(New JObject From {{"command", "state"}}, True)
        If CBool(State("admin")) Then Return
        If CBool(State("busy")) Then Throw New IOException("联机服务还在准备中，请稍等。")
        Await LinkRequestAsync(New JObject From {{"command", "exit"}})
        Await Task.Run(Sub()
                           If HelperProcess IsNot Nothing Then
                               If Not HelperProcess.WaitForExit(5000) Then Throw New IOException("联机后台还未退出，请稍后重试。")
                           Else
                               Thread.Sleep(600)
                           End If
                       End Sub)
        Await LinkRequestAsync(New JObject From {{"command", "state"}}, True, True)
    End Function

    Public Sub LinkClose(KeepRunning As Boolean)
        Try
            LinkRequestAsync(New JObject From {{"command", If(KeepRunning, "detach", "exit")}}).GetAwaiter().GetResult()
        Catch ex As Exception
            Logger.Warn(ex, "关闭联机后台时未收到确认")
        End Try
    End Sub
End Module
