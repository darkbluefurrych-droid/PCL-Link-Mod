Imports System.IO
Imports System.Net
Imports System.Text
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq

''' <summary>
''' PCL 联机修改版：自动接入 + 排队状态管理。
''' 使用自建联机服务器（无需验证码），超过人数上限时自动排队。
''' </summary>
Public Module ModLinkAuth

    Public Const ServerBase As String = "http://106.55.39.109:8088"
    Public Const RelayPeer As String = "tcp://106.55.39.109:11610"

    '当前状态（供 UI 与联机逻辑读取）
    Public Capacity As Integer = 300
    Public Players As Integer = 0
    Public Admitted As Boolean = True
    Public InQueue As Boolean = False
    Public QueuePosition As Integer = 0
    Public LastStateText As String = "正在自动接入…"

    Private _started As Boolean = False
    Private _owner As WeakReference(Of PageLinkMain) = Nothing
    Private _device As String = ""
    Private _token As String = ""
    Private _lastHeartbeat As Date = Date.MinValue
    Private ReadOnly _lock As New Object()

    Public ReadOnly Property AuthFilePath As String
        Get
            Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCL", "link_auth.dat")
        End Get
    End Property

    ''' <summary>
    ''' 页面加载时调用一次，启动后台自动接入线程。
    ''' </summary>
    Public Sub EnsureStarted(Owner As PageLinkMain)
        SyncLock _lock
            _owner = New WeakReference(Of PageLinkMain)(Owner)
            If _started Then Return
            _started = True
        End SyncLock
        LoadAuth()
        RunInNewThread(
        Sub()
            Do While True
                Try
                    EnsureToken()
                    RefreshStatus()
                    If (Date.Now - _lastHeartbeat).TotalSeconds >= 20 Then SendHeartbeat()
                Catch ex As Exception
                    Logger.Warn(ex, "联机接入失败")
                    LastStateText = "无法连接联机服务器，稍后自动重试"
                End Try
                Thread.Sleep(3000)
            Loop
        End Sub, "Link Auth")
    End Sub

    ''' <summary>
    ''' 每秒由页面时钟调用，刷新界面文字。
    ''' </summary>
    Public Sub UpdateUi()
        RunInUi(AddressOf UpdateUiCore)
    End Sub

    Private Sub UpdateUiCore()
        Try
            If _owner Is Nothing Then Return
            Dim owner As PageLinkMain = Nothing
            If Not _owner.TryGetTarget(owner) Then Return
            If owner Is Nothing Then Return
            Dim tbTitle = TryCast(owner.FindName("LabAuthTitle"), TextBlock)
            Dim tbDetail = TryCast(owner.FindName("LabAuthDetail"), TextBlock)
            Dim tbInfo = TryCast(owner.FindName("LabAuthInfo"), TextBlock)
            If tbTitle Is Nothing Then Return
            tbTitle.Text = "联机接入"
            If InQueue Then
                Dim Ahead As Integer = If(QueuePosition > 0, QueuePosition - 1, 0)
                tbDetail.Text = $"排队中 · 当前在线 {Players}/{Capacity}，你前面还有 {Ahead} 人"
                tbDetail.Foreground = New SolidColorBrush(Color.FromRgb(&HFF, &H8C, &H00))
            Else
                tbDetail.Text = $"已接入 · 无需验证码 · 当前在线 {Players}/{Capacity}"
                tbDetail.Foreground = New SolidColorBrush(Color.FromRgb(&H0A, &H7D, &H33))
            End If
            If tbInfo IsNot Nothing Then tbInfo.Text = LastStateText
        Catch ex As Exception
            Logger.Warn(ex, "刷新联机接入界面失败")
        End Try
    End Sub

    Private Sub LoadAuth()
        Try
            If Not File.Exists(AuthFilePath) Then Exit Try
            Dim obj = JObject.Parse(File.ReadAllText(AuthFilePath))
            _device = If(obj("device")?.ToString, "")
            _token = If(obj("token")?.ToString, "")
        Catch
        End Try
        If String.IsNullOrEmpty(_device) Then
            _device = "pc-" & Guid.NewGuid().ToString("N").Substring(0, 16)
            SaveAuth()
        End If
    End Sub

    Private Sub SaveAuth()
        Try
            Dim dir = Path.GetDirectoryName(AuthFilePath)
            If Not String.IsNullOrEmpty(dir) Then Directory.CreateDirectory(dir)
            Dim json As String = "{""server"":""" & ServerBase & """,""device"":""" & _device & """,""token"":""" & _token & """}"
            File.WriteAllText(AuthFilePath, json, New UTF8Encoding(False))
        Catch ex As Exception
            Logger.Warn(ex, "保存联机接入信息失败")
        End Try
    End Sub

    Private Sub EnsureToken()
        If Not String.IsNullOrEmpty(_token) Then Return
        Dim res = PostJson(ServerBase & "/api/verify", "{""device"":""" & _device & """}")
        If res IsNot Nothing AndAlso JBool(res, "ok") Then
            _token = If(res("token")?.ToString, "")
            SaveAuth()
            ApplyState(res)
            LastStateText = "已自动接入（无需验证码）"
        Else
            LastStateText = "自动接入失败，检查网络后自动重试"
        End If
    End Sub

    Private Sub RefreshStatus()
        If String.IsNullOrEmpty(_token) Then Return
        Dim res = PostJson(ServerBase & "/api/status",
            "{""device"":""" & _device & """,""token"":""" & _token & """}")
        If res Is Nothing Then
            LastStateText = "无法连接联机服务器，稍后自动重试"
            Return
        End If
        If Not JBool(res, "ok") Then
            _token = ""
            SaveAuth()
            LastStateText = "接入状态失效，正在重新接入…"
            Return
        End If
        ApplyState(res)
        LastStateText = "服务器连接正常"
    End Sub

    Private Sub SendHeartbeat()
        If String.IsNullOrEmpty(_token) Then Return
        Dim res = PostJson(ServerBase & "/api/heartbeat",
            "{""device"":""" & _device & """,""token"":""" & _token & """}")
        _lastHeartbeat = Date.Now
        If res Is Nothing Then
            LastStateText = "心跳失败，稍后重试"
            Return
        End If
        If Not JBool(res, "ok") Then
            _token = ""
            SaveAuth()
            Return
        End If
        ApplyState(res)
    End Sub

    Private Sub ApplyState(res As JObject)
        Try
            If res Is Nothing Then Return
            Admitted = JBool(res, "admitted")
            InQueue = JBool(res, "queued")
            Players = JInt(res, "players")
            Capacity = JInt(res, "capacity")
            QueuePosition = JInt(res, "position")
            If Capacity <= 0 Then Capacity = 300
        Catch ex As Exception
            Logger.Warn(ex, "解析联机状态失败")
        End Try
    End Sub

    Private Function JBool(obj As JObject, key As String) As Boolean
        Try
            Dim v = obj(key)
            If v Is Nothing Then Return False
            Return v.Value(Of Boolean)()
        Catch
            Return False
        End Try
    End Function

    Private Function JInt(obj As JObject, key As String) As Integer
        Try
            Dim v = obj(key)
            If v Is Nothing Then Return 0
            Return v.Value(Of Integer)()
        Catch
            Return 0
        End Try
    End Function

    Private Function PostJson(url As String, body As String) As JObject
        Try
            Dim req = CType(WebRequest.Create(url), HttpWebRequest)
            req.Method = "POST"
            req.ContentType = "application/json"
            req.Timeout = 8000
            req.ReadWriteTimeout = 8000
            Dim data = Encoding.UTF8.GetBytes(body)
            req.ContentLength = data.Length
            Using s = req.GetRequestStream()
                s.Write(data, 0, data.Length)
            End Using
            Using resp = req.GetResponse()
                Using sr As New StreamReader(resp.GetResponseStream(), Encoding.UTF8)
                    Return JObject.Parse(sr.ReadToEnd())
                End Using
            End Using
        Catch ex As WebException
            Try
                If ex.Response IsNot Nothing Then
                    Using sr As New StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8)
                        Return JObject.Parse(sr.ReadToEnd())
                    End Using
                End If
            Catch
            End Try
            Return Nothing
        Catch
            Return Nothing
        End Try
    End Function

End Module
