Imports System.Collections.ObjectModel
Imports System.Collections.Specialized
Imports System.Net.Http
Imports System.Net.Sockets
Imports Newtonsoft.Json.Linq
Imports PCL.Core.App
Imports PCL.Core.Link
Imports PCL.Core.Link.Lobby

Public Class PageMergilinkLeft
    Inherits MyPageRight
    Private Shared SshProcess As Process
    Private Shared IsExitHandlerAdded As Boolean = False

    Private Class NodeInfo
        Public Property Name As String
        Public Property Host As String
        Public Property Port As Integer = 2222
        Public Property Region As String = ""
        Public Property Ping As Integer = -1
    End Class

    Private Const NodeListApiUrl As String = "https://nodelist.mergi.ink/node.json"

    Private Async Sub PageMergilinkLeft_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Await LobbyService.InitializeAsync()
        RefreshWorlds()
        AddHandler LobbyService.DiscoveredWorlds.CollectionChanged, AddressOf OnDiscoveredWorldsChanged

        ' Initialize Node List from API
        If ComboNodeList.Items.Count = 0 Then
            Task.Run(Sub() LoadNodesFromApi())
        End If

        If Not IsExitHandlerAdded Then
            AddHandler Application.Current.Exit, AddressOf OnAppExit
            IsExitHandlerAdded = True
        End If

        ' Restore UI state
        If SshProcess IsNot Nothing AndAlso Not SshProcess.HasExited Then
            BtnStart.Visibility = Visibility.Collapsed
            BtnStop.Visibility = Visibility.Visible
            TextPort.IsEnabled = False
            BtnSwitchMode.IsEnabled = False
            ComboWorldList.IsEnabled = False
            BtnRefresh.IsEnabled = False
            ComboNodeList.IsEnabled = False
            BtnRefreshNodes.IsEnabled = False
            
            ' Attach handlers for this instance
            AddHandler SshProcess.OutputDataReceived, AddressOf OutputHandler
            AddHandler SshProcess.ErrorDataReceived, AddressOf OutputHandler
            AddHandler SshProcess.Exited, AddressOf OnSshExited
        Else
            BtnStart.Visibility = Visibility.Visible
            BtnStop.Visibility = Visibility.Collapsed
            TextPort.IsEnabled = True
            BtnSwitchMode.IsEnabled = True
            ComboWorldList.IsEnabled = True
            BtnRefresh.IsEnabled = True
            ComboNodeList.IsEnabled = True
            BtnRefreshNodes.IsEnabled = True
        End If
    End Sub

    Private Sub PageMergilinkLeft_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        RemoveHandler LobbyService.DiscoveredWorlds.CollectionChanged, AddressOf OnDiscoveredWorldsChanged
        
        If SshProcess IsNot Nothing Then
            RemoveHandler SshProcess.OutputDataReceived, AddressOf OutputHandler
            RemoveHandler SshProcess.ErrorDataReceived, AddressOf OutputHandler
            RemoveHandler SshProcess.Exited, AddressOf OnSshExited
        End If
    End Sub

    Private Shared Sub OnAppExit(sender As Object, e As ExitEventArgs)
        If SshProcess IsNot Nothing AndAlso Not SshProcess.HasExited Then
            Try
                SshProcess.Kill()
            Catch
            End Try
        End If
    End Sub

    Private Sub RefreshWorlds()
        LobbyService.DiscoverWorldAsync()
        UpdateWorldList(LobbyService.DiscoveredWorlds)
    End Sub

    Private Sub BtnRefresh_Click(sender As Object, e As EventArgs) Handles BtnRefresh.Click
        RefreshWorlds()
    End Sub

    Private Sub OnDiscoveredWorldsChanged(sender As Object, e As NotifyCollectionChangedEventArgs)
        Dispatcher.Invoke(Sub()
            UpdateWorldList(LobbyService.DiscoveredWorlds)
        End Sub)
    End Sub

    Private Sub UpdateWorldList(worlds As ObservableCollection(Of FoundWorld))
        Dim selectedPort As Integer = -1
        If ComboWorldList.SelectedItem IsNot Nothing Then
            selectedPort = CType(ComboWorldList.SelectedItem.Tag, Integer)
        End If

        ComboWorldList.Items.Clear()
        For Each world As FoundWorld In worlds
            Dim item As New MyComboBoxItem() With {
                .Tag = world.Port,
                .Content = world.Name & " (" & world.Port & ")"
            }
            ComboWorldList.Items.Add(item)
            If world.Port = selectedPort Then
                ComboWorldList.SelectedItem = item
            End If
        Next
        
        If ComboWorldList.Items.Count > 0 AndAlso ComboWorldList.SelectedIndex = -1 Then
            ComboWorldList.SelectedIndex = 0
        End If
    End Sub

    Private Sub BtnSwitchMode_Click(sender As Object, e As EventArgs) Handles BtnSwitchMode.Click
        If TextPort.Visibility = Visibility.Visible Then
            ' Switch to Scan
            TextPort.Visibility = Visibility.Collapsed
            ComboWorldList.Visibility = Visibility.Visible
            BtnRefresh.Visibility = Visibility.Visible
            BtnSwitchMode.Text = "手动"
            RefreshWorlds()
        Else
            ' Switch to Manual
            TextPort.Visibility = Visibility.Visible
            ComboWorldList.Visibility = Visibility.Collapsed
            BtnRefresh.Visibility = Visibility.Collapsed
            BtnSwitchMode.Text = "扫描"
        End If
    End Sub

    Private Sub BtnStart_Click(sender As Object, e As EventArgs) Handles BtnStart.Click
        Try
            Dim port As String
            If TextPort.Visibility = Visibility.Visible Then
                port = TextPort.Text
            Else
                If ComboWorldList.SelectedItem Is Nothing Then
                    Log("请先选择一个世界")
                    Return
                End If
                port = CType(ComboWorldList.SelectedItem.Tag, Integer).ToString()
            End If

            If String.IsNullOrWhiteSpace(port) Then
                Log("端口不能为空")
                Return
            End If

            Dim selectedNode As NodeInfo = CType(ComboNodeList.SelectedItem, MyComboBoxItem).Tag
            Dim nodeHost As String = selectedNode.Host
            Dim nodePort As Integer = selectedNode.Port

            Dim processStartInfo As New ProcessStartInfo()
            processStartInfo.FileName = "ssh"
            ' Added -v for verbose output, -o StrictHostKeyChecking=no to avoid prompts, -tt to force pseudo-tty (might help with buffering)
            ' Using NUL for UserKnownHostsFile to avoid saving the host key
            processStartInfo.Arguments = $"-o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL -R 1:localhost:{port} -p {nodePort} {nodeHost}"
            processStartInfo.RedirectStandardOutput = True
            processStartInfo.RedirectStandardError = True
            processStartInfo.UseShellExecute = False
            processStartInfo.CreateNoWindow = True
            processStartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8
            processStartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8

            SshProcess = New Process()
            SshProcess.StartInfo = processStartInfo
            AddHandler SshProcess.OutputDataReceived, AddressOf OutputHandler
            AddHandler SshProcess.ErrorDataReceived, AddressOf OutputHandler
            
            SshProcess.EnableRaisingEvents = True
            AddHandler SshProcess.Exited, AddressOf OnSshExited
            
            SshProcess.Start()
            SshProcess.BeginOutputReadLine()
            SshProcess.BeginErrorReadLine()

            'Log($"SSH 启动: ssh {processStartInfo.Arguments}")
            
            BtnStart.Visibility = Visibility.Collapsed
            BtnStop.Visibility = Visibility.Visible
            TextPort.IsEnabled = False
            BtnSwitchMode.IsEnabled = False
            ComboWorldList.IsEnabled = False
            BtnRefresh.IsEnabled = False
            ComboNodeList.IsEnabled = False
            BtnRefreshNodes.IsEnabled = False

        Catch ex As Exception
            Log("启动失败: " & ex.Message)
        End Try
    End Sub

    Private Sub BtnStop_Click(sender As Object, e As EventArgs) Handles BtnStop.Click
        StopSsh()
    End Sub

    Private Sub StopSsh()
        If SshProcess IsNot Nothing Then
            Try
                If Not SshProcess.HasExited Then
                    SshProcess.Kill()
                End If
                SshProcess.Dispose()
            Catch ex As Exception
                Log("停止失败: " & ex.Message)
            End Try
        End If
        SshProcess = Nothing
        
        Dispatcher.Invoke(Sub()
            BtnStart.Visibility = Visibility.Visible
            BtnStop.Visibility = Visibility.Collapsed
            TextPort.IsEnabled = True
            BtnSwitchMode.IsEnabled = True
            ComboWorldList.IsEnabled = True
            BtnRefresh.IsEnabled = True
            ComboNodeList.IsEnabled = True
            BtnRefreshNodes.IsEnabled = True
            Log("SSH 已停止")
        End Sub)
    End Sub

    Private Sub OutputHandler(sender As Object, e As DataReceivedEventArgs)
        If Not String.IsNullOrEmpty(e.Data) Then
            Log(e.Data)
        End If
    End Sub

    Private Sub OnSshExited(sender As Object, e As EventArgs)
        Dispatcher.Invoke(Sub()
            If SshProcess IsNot Nothing Then
                Log("SSH 进程已退出")
                StopSsh()
            End If
        End Sub)
    End Sub

    Private Sub Log(text As String)
        Dispatcher.Invoke(Sub()
            TextLog.AppendText(text & Environment.NewLine)
            TextLog.ScrollToEnd()
        End Sub)
    End Sub

    Private Async Function TcpPing(host As String, port As Integer) As Task(Of Integer)
        Try
            Using client As New TcpClient()
                Dim sw As New Stopwatch()
                sw.Start()
                
                Dim connectTask = client.ConnectAsync(host, port)
                Dim timeoutTask = Task.Delay(5000)
                
                Dim completedTask = Await Task.WhenAny(connectTask, timeoutTask)
                sw.Stop()
                
                If completedTask Is connectTask AndAlso client.Connected Then
                    Return CInt(sw.ElapsedMilliseconds)
                Else
                    Return -1
                End If
            End Using
        Catch ex As Exception
            Return -1
        End Try
    End Function

    Private Async Sub PingAllNodes()
        ' Get items count on UI thread
        Dim itemCount As Integer = 0
        Dispatcher.Invoke(Sub()
            itemCount = ComboNodeList.Items.Count
        End Sub)
        
        For i As Integer = 0 To itemCount - 1
            Dim index = i
            
            ' Get node info on UI thread
            Dim nodeInfo As NodeInfo = Nothing
            Dispatcher.Invoke(Sub()
                Dim item = CType(ComboNodeList.Items(index), MyComboBoxItem)
                nodeInfo = CType(item.Tag, NodeInfo)
            End Sub)
            
            ' Perform ping test (this can be done off UI thread)
            Dim ping = Await TcpPing(nodeInfo.Host, nodeInfo.Port)
            nodeInfo.Ping = ping
            
            ' Update UI on UI thread
            Dispatcher.Invoke(Sub()
                Dim item = CType(ComboNodeList.Items(index), MyComboBoxItem)
                If ping >= 0 Then
                    item.Content = $"{nodeInfo.Name} ({ping}ms)"
                Else
                    item.Content = $"{nodeInfo.Name} (超时)"
                End If
            End Sub)
        Next
    End Sub

    Private Async Sub LoadNodesFromApi()
        Try
            ' Show loading message
            Dispatcher.Invoke(Sub()
                ComboNodeList.Items.Clear()
                ComboNodeList.Items.Add(New MyComboBoxItem() With {.Content = "正在加载节点列表..."})
            End Sub)

            ' Fetch node list from API
            Using client As New HttpClient()
                client.Timeout = TimeSpan.FromSeconds(10)
                Dim response = Await client.GetStringAsync(NodeListApiUrl)
                Dim json = JObject.Parse(response)
                Dim nodes = json("nodes")

                ' Clear loading message and add nodes
                Dispatcher.Invoke(Sub()
                    ComboNodeList.Items.Clear()
                End Sub)

                If nodes IsNot Nothing Then
                    For Each nodeJson In nodes
                        Dim nodeInfo As New NodeInfo With {
                            .Name = nodeJson("description").ToString(),
                            .Host = nodeJson("host").ToString(),
                            .Port = If(nodeJson("port") IsNot Nothing, CInt(nodeJson("port")), 2222),
                            .Region = If(nodeJson("region") IsNot Nothing, nodeJson("region").ToString(), "")
                        }

                        Dispatcher.Invoke(Sub()
                            Dim displayName = If(String.IsNullOrEmpty(nodeInfo.Region), 
                                nodeInfo.Name, 
                                $"{nodeInfo.Name} ({nodeInfo.Region})")
                            ComboNodeList.Items.Add(New MyComboBoxItem() With {
                                .Content = $"{displayName} (检测中...)",
                                .Tag = nodeInfo
                            })
                        End Sub)
                    Next

                    ' Select first node
                    Dispatcher.Invoke(Sub()
                        If ComboNodeList.Items.Count > 0 Then
                            ComboNodeList.SelectedIndex = 0
                        End If
                    End Sub)

                    ' Start ping test
                    Await Task.Run(Sub() PingAllNodes())
                End If
            End Using

        Catch ex As Exception
            ' Don't show default nodes on error, just show error message
            Log($"加载节点列表失败: {ex.Message}")
            
            Dispatcher.Invoke(Sub()
                ComboNodeList.Items.Clear()
                ComboNodeList.Items.Add(New MyComboBoxItem() With {.Content = "加载失败，请点击刷新节点重试"})
            End Sub)
        End Try
    End Sub

    Private Sub BtnRefreshNodes_Click(sender As Object, e As EventArgs) Handles BtnRefreshNodes.Click
        Task.Run(Sub() LoadNodesFromApi())
    End Sub
End Class
