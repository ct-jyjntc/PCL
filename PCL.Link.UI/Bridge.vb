Imports System.Runtime.CompilerServices

Public NotInheritable Class Bridge
    Private Shared ReadOnly Pages As New ConditionalWeakTable(Of Object, LinkPage)
    Private Shared CurrentPage As LinkPage
    Private Shared ExitChoice As Integer = 1

    Public Shared Sub Attach(HostObject As Object)
        Dim Host = TryCast(HostObject, MyPageRight)
        If Host Is Nothing Then Throw New InvalidOperationException("联机补丁与当前 PCL 页面接口不兼容。")
        Dim Page As New LinkPage
        Pages.Add(HostObject, Page)
        CurrentPage = Page
        Host.Child = Page
        NameScope.SetNameScope(Host, New NameScope())
        Host.RegisterName("PanBack", Page.GetScroll())
        Host.PanScroll = "PanBack"
    End Sub

    Public Shared Sub AttachHome(HostObject As Object)
        Dim Host = TryCast(HostObject, MyPageRight)
        Dim Panel = TryCast(Host.FindName("PanMain"), StackPanel)
        If Panel Is Nothing Then Throw New InvalidOperationException("启动页右侧容器不兼容。")
        Dim Page As New LinkPage
        Dim Scroll = Page.GetScroll()
        Dim Body = TryCast(Scroll.Content, StackPanel)
        Scroll.Content = Nothing
        Body.Margin = New Thickness(0)
        Page.Content = Body
        'PanCustom stays independent, so a custom homepage refresh cannot remove us.
        Panel.Children.Insert(1, Page)
        Pages.Add(HostObject, Page)
        CurrentPage = Page
    End Sub

    Public Shared Function BeforeExit(SendWarning As Boolean) As Boolean
        ExitChoice = If(CurrentPage Is Nothing, 1, CurrentPage.ChooseExit(Not SendWarning))
        Return ExitChoice = 3
    End Function

    Public Shared Sub CommitExit()
        If CurrentPage IsNot Nothing Then CurrentPage.CommitClose(ExitChoice = 2)
    End Sub

    Public Shared Function TryExit(Host As Object, Silent As Boolean, Closing As Boolean) As Boolean
        Dim Page As LinkPage = Nothing
        If Not Pages.TryGetValue(Host, Page) Then Return False
        'Homepage sessions are committed by EndProgramForce after other close warnings.
        Return False
    End Function

    Public Shared Function ValidateCodeFormat(Code As String) As String
        Return LinkPage.ValidateCodeFormat(Code)
    End Function

    Public Shared Sub Join()
        OnUi(Sub()
                 Dim Code = ModMain.MyMsgBoxInput("加入网络", "输入朋友发来的 6 位网络 ID。")
                 If Code IsNot Nothing Then Join(Code)
             End Sub)
    End Sub

    Public Shared Sub Join(Code As String)
        Dim ErrorText = ValidateCodeFormat(Code)
        If ErrorText IsNot Nothing Then
            ModMain.Hint(ErrorText, ModMain.HintType.Red, True)
            Return
        End If
        OnUi(Sub()
                 ModMain.FrmMain.PageChange(FormMain.PageType.Launch)
                 If CurrentPage IsNot Nothing Then CurrentPage.QueueJoin(Code.Trim().ToUpperInvariant())
             End Sub)
    End Sub

    Private Shared Sub OnUi(Action As Action)
        Dim Ui = System.Windows.Application.Current.Dispatcher
        If Ui.CheckAccess() Then Action() Else Ui.BeginInvoke(Action)
    End Sub
End Class
