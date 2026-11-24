Imports System.Windows.Threading

Public Class PageLinkMain
    Public Enum LinkStates
        Waiting
        Loading
        Failed
        Finished
    End Enum
    Public Shared LinkState As LinkStates = LinkStates.Waiting
    Private ReadOnly PollTimer As New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(1)}
    Private Latest As JObject
    Private Polling As Boolean
    Private CommandPending As Boolean
    Private HelperKnown As Boolean
    Private Closing As Boolean
    Private BootstrapSent As Boolean
    Private PlayerName As String
    Private SyncedName As String
    Private Shared AutoJoinCode As String
    Private RenderedRooms As String
    Private ReadOnly PeerRows As New Dictionary(Of String, TextBlock)

    Public Sub New()
        InitializeComponent()
        AddHandler PollTimer.Tick, AddressOf Poll
        PollTimer.Start()
    End Sub

    Public Shared Function ResolvePlayerName() As String
        Try
            Dim Kind = Settings.Get(Of McLoginType)("LoginType")
            If Kind = McLoginType.Legacy Then
                Dim Data = CType(PageLoginLegacy.GetLoginData(), McLoginLegacy)
                If PageLoginLegacy.IsVaild(Data) = "" Then Return Data.UserName.Trim()
                Return Nothing
            End If
            If McLoginAble() <> "" Then Return Nothing
            Dim Name As String = Nothing
            Select Case Kind
                Case McLoginType.Ms
                    If McLoginMsLoader.State = LoadState.Finished Then Name = McLoginMsLoader.Output.Name
                Case McLoginType.Nide
                    If McLoginNideLoader.State = LoadState.Finished Then Name = McLoginNideLoader.Output.Name
                Case McLoginType.Auth
                    If McLoginAuthLoader.State = LoadState.Finished Then Name = McLoginAuthLoader.Output.Name
            End Select
            If Not String.IsNullOrWhiteSpace(Name) AndAlso Name = McLoginName() Then Return Name.Trim()
        Catch
            'The game login controls may not have been created yet.
        End Try
        Return Nothing
    End Function

    Private Async Sub EnterPage() Handles Me.Loaded
        PlayerName = ResolvePlayerName()
        If Not BootstrapSent Then Await BootstrapAsync()
        Await RefreshAsync()
        If AutoJoinCode IsNot Nothing Then
            TextCode.Text = AutoJoinCode
            AutoJoinCode = Nothing
            If Not String.IsNullOrEmpty(PlayerName) Then JoinClick()
        End If
    End Sub

    Private Async Sub Poll(sender As Object, e As EventArgs)
        If Closing Then Return
        PlayerName = ResolvePlayerName()
        If HelperKnown Then Await RefreshAsync()
        If Latest IsNot Nothing Then RenderState()
        If HelperKnown AndAlso Latest IsNot Nothing AndAlso Not CBool(Latest("busy")) AndAlso
            Not String.IsNullOrEmpty(PlayerName) AndAlso PlayerName <> SyncedName AndAlso Not CommandPending Then
            Try
                Await LinkRequestAsync(New JObject From {{"command", "player"}, {"name", PlayerName}})
                SyncedName = PlayerName
            Catch ex As Exception
                ShowError(ex.Message)
            End Try
        End If
    End Sub

    Private Async Function BootstrapAsync() As Task
        If CommandPending OrElse Closing Then Return
        BootstrapSent = True
        CommandPending = True
        Try
            Await LinkRequestAsync(New JObject From {{"command", "auto"},
                {"nickname", If(String.IsNullOrEmpty(PlayerName), Environment.UserName, PlayerName)}}, True)
            HelperKnown = True
        Catch ex As Exception
            BootstrapSent = False
            ShowError(ex.Message)
            BtnRetry.Visibility = Visibility.Visible
        Finally
            CommandPending = False
        End Try
    End Function

    Private Async Function RefreshAsync() As Task
        If Polling OrElse Closing Then Return
        Polling = True
        Try
            Latest = Await LinkRequestAsync(New JObject From {{"command", "state"}})
            HelperKnown = True
            RenderState()
        Catch ex As Exception
            If HelperKnown Then ShowError(ex.Message)
            HelperKnown = False
            BtnRetry.Visibility = Visibility.Visible
        Finally
            Polling = False
        End Try
    End Function

    Private Async Function SendAsync(Request As JObject, Optional NeedsAdmin As Boolean = False) As Task
        If CommandPending OrElse Closing Then Return
        CommandPending = True
        HintError.Visibility = Visibility.Collapsed
        Try
            If NeedsAdmin Then
                If String.IsNullOrEmpty(ResolvePlayerName()) Then Throw New IOException("先登录游戏账号或填写有效的离线玩家名。")
                Await LinkEnsureElevatedAsync()
            End If
            Await LinkRequestAsync(Request, True, NeedsAdmin)
            HelperKnown = True
            Await RefreshAsync()
        Catch ex As Exception
            ShowError(ex.Message)
        Finally
            CommandPending = False
            If Latest IsNot Nothing Then RenderState(False)
        End Try
    End Function

    Private Sub ShowError(Message As String)
        HintError.Text = Message
        HintError.Visibility = Visibility.Visible
    End Sub

    Private Sub RenderState(Optional ClearError As Boolean = True)
        Dim Busy = CBool(Latest("busy")) OrElse CommandPending
        Dim Version = Latest("discoveryVersion")
        Dim Updated = Version IsNot Nothing AndAlso Version.ToObject(Of Integer)() >= 2
        Dim LoggedIn = CBool(Latest("loggedIn"))
        Dim State = CType(Latest("state"), JObject)
        Dim Connected = CBool(State("running")) AndAlso CBool(State("roomReady"))
        Dim Connecting = CStr(Latest("operation")) = "正在创建网络" OrElse CStr(Latest("operation")) = "正在加入网络"
        LinkState = If(Connected, LinkStates.Finished, If(Connecting, LinkStates.Loading, LinkStates.Waiting))
        LabPlayer.Text = If(String.IsNullOrEmpty(PlayerName), "先登录游戏账号", PlayerName)
        LabStatus.Text = If(Connected, "已连接", If(Connecting, CStr(Latest("operation")), If(Not LoggedIn OrElse Busy, "准备联机中…", "尚未连接")))
        Dim Ready = Updated AndAlso LoggedIn AndAlso Not Busy AndAlso Not String.IsNullOrEmpty(PlayerName)
        BtnCreate.IsEnabled = Ready
        BtnJoin.IsEnabled = Ready
        TextCode.IsEnabled = Ready
        PanNetworkActions.Visibility = If(Connected OrElse Connecting, Visibility.Collapsed, Visibility.Visible)
        PanConnectedActions.Visibility = If(Connected, Visibility.Visible, Visibility.Collapsed)
        BtnCancel.Visibility = If(Connecting, Visibility.Visible, Visibility.Collapsed)
        BtnLeave.IsEnabled = Not CommandPending
        LabNetworkId.Visibility = If(Connected, Visibility.Visible, Visibility.Collapsed)
        LabNetworkIp.Visibility = LabNetworkId.Visibility
        LabNetworkId.Text = CStr(State("inviteCode"))
        LabNetworkIp.Text = CStr(State("virtualIp"))
        CardRooms.Visibility = If(Connected, Visibility.Visible, Visibility.Collapsed)
        CardPeers.Visibility = CardRooms.Visibility
        Dim ErrorText = If(CStr(Latest("error")), CStr(State("error")))
        If Not Updated Then
            LabStatus.Text = "需要重启联机后台"
            ShowError("检测到旧版联机后台。请先退出旧启动器及联机网络，不要选择保持联机，然后打开新版。")
        ElseIf Not String.IsNullOrEmpty(ErrorText) Then
            ShowError(ErrorText)
            BtnRetry.Visibility = If(Not Connected AndAlso Not Busy, Visibility.Visible, Visibility.Collapsed)
        ElseIf ClearError Then
            HintError.Visibility = Visibility.Collapsed
            BtnRetry.Visibility = Visibility.Collapsed
        End If
        Dim Rooms = CType(Latest("rooms"), JArray)
        LabDiscovery.Text = If(CStr(Latest("discoveryError")), If(Rooms.Count = 0, "尚未发现世界", "已发现 " & Rooms.Count & " 个世界"))
        Dim RoomKey = Rooms.ToString(Formatting.None)
        If RoomKey <> RenderedRooms Then
            RenderedRooms = RoomKey
            PanRooms.Children.Clear()
            For Each Room As JObject In Rooms
                Dim Address = If(CBool(Room("isLocal")), CStr(Room("address")), "127.0.0.1:" & CStr(Room("localPort")))
                Dim Row As New MyButton With {.Text = CStr(Room("motd")), .Height = 35, .Margin = New Thickness(0, 0, 0, 8), .ToolTip = "复制连接地址"}
                AddHandler Row.Click, Sub() ClipboardSet(Address, True)
                PanRooms.Children.Add(Row)
            Next
        End If
        Dim Peers = CType(State("members"), JArray)
        Dim CurrentIps As New HashSet(Of String)
        For Each Peer As JObject In Peers
            Dim Ip = CStr(Peer("ip"))
            CurrentIps.Add(Ip)
            Dim Text = CStr(Peer("name")) & If(CBool(Peer("isSelf")), " · 本机", "") & "    " & Ip
            If CInt(Peer("ping")) >= 0 Then Text &= "    " & CStr(Peer("ping")) & " ms"
            Dim Row As TextBlock = Nothing
            If Not PeerRows.TryGetValue(Ip, Row) Then
                Row = New TextBlock With {.TextWrapping = TextWrapping.Wrap, .Margin = New Thickness(0, 4, 0, 8)}
                PeerRows.Add(Ip, Row)
                PanPeers.Children.Add(Row)
            End If
            If Row.Text <> Text Then Row.Text = Text
        Next
        For Each Ip In PeerRows.Keys.Where(Function(Address) Not CurrentIps.Contains(Address)).ToArray()
            PanPeers.Children.Remove(PeerRows(Ip))
            PeerRows.Remove(Ip)
        Next
    End Sub

    Private Async Sub RetryClick() Handles BtnRetry.Click
        Await BootstrapAsync()
        Await RefreshAsync()
    End Sub
    Private Async Sub CreateClick() Handles BtnCreate.Click
        Await SendAsync(New JObject From {{"command", "create"}, {"playerName", PlayerName}}, True)
    End Sub
    Private Async Sub JoinClick() Handles BtnJoin.Click
        Dim Problem = ValidateCodeFormat(TextCode.Text.Trim())
        If Problem IsNot Nothing Then
            ShowError(Problem)
            Return
        End If
        Await SendAsync(New JObject From {{"command", "join"}, {"code", TextCode.Text.Trim().ToUpperInvariant()}, {"playerName", PlayerName}}, True)
    End Sub
    Private Async Sub LeaveClick() Handles BtnLeave.Click, BtnCancel.Click
        Await SendAsync(New JObject From {{"command", "leave"}})
    End Sub
    Private Sub CopyCode() Handles BtnCopyCode.Click
        ClipboardSet("网络 ID【" & LabNetworkId.Text & "】", True)
    End Sub
    Private Sub CopyIp() Handles BtnCopyIp.Click
        ClipboardSet(LabNetworkIp.Text, True)
    End Sub

    Public Function ChooseExit(Silent As Boolean) As Integer
        If Silent OrElse (LinkState <> LinkStates.Finished AndAlso LinkState <> LinkStates.Loading) Then Return 1
        Return MyMsgBox("关闭启动器后是否保持联机？", "关闭启动器", "退出联机", "保持联机", "取消")
    End Function
    Public Sub CommitClose(KeepRunning As Boolean)
        If Closing Then Return
        Closing = True
        PollTimer.Stop()
        If HelperKnown Then LinkClose(KeepRunning)
    End Sub
    Public Function TryExit(Silent As Boolean, ClosingLauncher As Boolean) As Boolean
        Dim Choice = ChooseExit(Silent)
        If Choice = 3 Then Return True
        CommitClose(Choice = 2)
        Return False
    End Function
    Public Shared Function ValidateCodeFormat(Code As String) As String
        If Code Is Nothing OrElse Not System.Text.RegularExpressions.Regex.IsMatch(Code.Trim(), "^[A-Za-z0-9]{6}$") Then Return "网络 ID 应为 6 位字母或数字。"
        Return Nothing
    End Function
    Public Shared Sub Join()
        Dim Code = MyMsgBoxInput("加入网络", "输入 6 位网络 ID。")
        If Code IsNot Nothing Then Join(Code)
    End Sub
    Public Shared Sub Join(Code As String)
        AutoJoinCode = Code
        FrmMain.PageChange(FormMain.PageType.Link, FormMain.PageSubType.LinkMain)
    End Sub
End Class
