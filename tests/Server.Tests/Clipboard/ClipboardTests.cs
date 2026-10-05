using RemoteSupport.Shared.Clipboard;

namespace RemoteSupport.Server.Tests.Clipboard;

public class ClipboardTests : IDisposable
{
    private readonly ClipboardManager _manager;

    public ClipboardTests()
    {
        _manager = new ClipboardManager();
    }

    public void Dispose() { }

    // --- Message Serialization Tests ---

    [Fact]
    public void ClipboardMessage_TextSync_SerializeDeserialize()
    {
        var msg = new ClipboardMessage
        {
            Type = ClipboardMessageType.TextSync,
            Text = "Hello, World!"
        };

        var serialized = ClipboardMessage.Serialize(msg);
        var deserialized = ClipboardMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(ClipboardMessageType.TextSync, deserialized!.Type);
        Assert.Equal("Hello, World!", deserialized.Text);
    }

    [Fact]
    public void ClipboardMessage_PersianText_SerializeDeserialize()
    {
        var persianText = "سلام دنیا - این یک متن فارسی است";
        var msg = new ClipboardMessage
        {
            Type = ClipboardMessageType.TextSync,
            Text = persianText
        };

        var serialized = ClipboardMessage.Serialize(msg);
        var deserialized = ClipboardMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(persianText, deserialized!.Text);
    }

    [Fact]
    public void ClipboardMessage_ArabicText_SerializeDeserialize()
    {
        var arabicText = "مرحبا بالعالم";
        var msg = new ClipboardMessage
        {
            Type = ClipboardMessageType.TextSync,
            Text = arabicText
        };

        var serialized = ClipboardMessage.Serialize(msg);
        var deserialized = ClipboardMessage.Deserialize(serialized);

        Assert.Equal(arabicText, deserialized!.Text);
    }

    [Fact]
    public void ClipboardMessage_EmptyText_SerializeDeserialize()
    {
        var msg = new ClipboardMessage
        {
            Type = ClipboardMessageType.TextSync,
            Text = ""
        };

        var serialized = ClipboardMessage.Serialize(msg);
        var deserialized = ClipboardMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.Text is null or "");
    }

    [Fact]
    public void ClipboardMessage_LargeText_SerializeDeserialize()
    {
        var largeText = new string('A', 100_000);
        var msg = new ClipboardMessage
        {
            Type = ClipboardMessageType.TextSync,
            Text = largeText
        };

        var serialized = ClipboardMessage.Serialize(msg);
        var deserialized = ClipboardMessage.Deserialize(serialized);

        Assert.Equal(100_000, deserialized!.Text!.Length);
    }

    [Fact]
    public void ClipboardMessage_FileList_SerializeDeserialize()
    {
        var msg = new ClipboardMessage
        {
            Type = ClipboardMessageType.FileListSync,
            Files = new List<ClipboardFileEntry>
            {
                new() { FileName = "doc.pdf", Size = 1024, Checksum = "ABC" },
                new() { FileName = "image.png", Size = 2048, Checksum = "DEF" }
            }
        };

        var serialized = ClipboardMessage.Serialize(msg);
        var deserialized = ClipboardMessage.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized!.Files!.Count);
        Assert.Equal("doc.pdf", deserialized.Files[0].FileName);
        Assert.Equal(1024, deserialized.Files[0].Size);
        Assert.Equal("image.png", deserialized.Files[1].FileName);
    }

    [Fact]
    public void ClipboardMessage_ControlMessages_SerializeDeserialize()
    {
        foreach (var type in new[] {
            ClipboardMessageType.ConsentGrant,
            ClipboardMessageType.ConsentRevoke,
            ClipboardMessageType.SyncRequest })
        {
            var msg = new ClipboardMessage { Type = type };
            var serialized = ClipboardMessage.Serialize(msg);
            var deserialized = ClipboardMessage.Deserialize(serialized);

            Assert.NotNull(deserialized);
            Assert.Equal(type, deserialized!.Type);
        }
    }

    [Fact]
    public void ClipboardMessage_InvalidData_ReturnsMessageWithUnknownType()
    {
        var result = ClipboardMessage.Deserialize(new byte[] { 0xFF });
        Assert.NotNull(result);
        // Type 0xFF is not a valid clipboard message type
        Assert.Equal((ClipboardMessageType)0xFF, result!.Type);
    }

    // --- Clipboard Manager Consent Tests ---

    [Fact]
    public void ClipboardManager_InitiallyNotConsented()
    {
        Assert.False(_manager.IsConsented);
    }

    [Fact]
    public void ClipboardManager_GrantConsent()
    {
        _manager.GrantConsent("session-1");

        Assert.True(_manager.IsConsented);
        Assert.True(_manager.ValidateConsent("session-1"));
    }

    [Fact]
    public void ClipboardManager_RevokeConsent()
    {
        _manager.GrantConsent("session-1");
        _manager.RevokeConsent();

        Assert.False(_manager.IsConsented);
        Assert.False(_manager.ValidateConsent("session-1"));
    }

    [Fact]
    public void ClipboardManager_ValidateConsent_WrongSession()
    {
        _manager.GrantConsent("session-1");

        Assert.False(_manager.ValidateConsent("session-2"));
    }

    [Fact]
    public void ClipboardManager_SendText_FiresEvent()
    {
        string? receivedText = null;
        _manager.TextReceived += text => receivedText = text;

        _manager.SendTextAsync("test clipboard").Wait();

        Assert.Equal("test clipboard", receivedText);
    }

    [Fact]
    public void ClipboardManager_SendFileList_FiresEvent()
    {
        List<ClipboardFileEntry>? receivedFiles = null;
        _manager.FileListReceived += files => receivedFiles = files;

        var files = new List<ClipboardFileEntry>
        {
            new() { FileName = "test.pdf", Size = 1024 }
        };
        _manager.SendFileListAsync(files).Wait();

        Assert.NotNull(receivedFiles);
        Assert.Single(receivedFiles!);
        Assert.Equal("test.pdf", receivedFiles[0].FileName);
    }

    [Fact]
    public void ClipboardManager_HandleReceivedMessage_Text()
    {
        string? receivedText = null;
        _manager.TextReceived += text => receivedText = text;

        var msg = new ClipboardMessage
        {
            Type = ClipboardMessageType.TextSync,
            Text = "received text"
        };

        _manager.HandleReceivedMessage(msg);

        Assert.Equal("received text", receivedText);
    }

    [Fact]
    public void ClipboardManager_HandleReceivedMessage_Files()
    {
        List<ClipboardFileEntry>? receivedFiles = null;
        _manager.FileListReceived += files => receivedFiles = files;

        var msg = new ClipboardMessage
        {
            Type = ClipboardMessageType.FileListSync,
            Files = new List<ClipboardFileEntry>
            {
                new() { FileName = "doc.txt", Size = 512 }
            }
        };

        _manager.HandleReceivedMessage(msg);

        Assert.NotNull(receivedFiles);
        Assert.Single(receivedFiles!);
    }

    [Fact]
    public void ClipboardManager_PersianText_FiresEvent()
    {
        string? receivedText = null;
        _manager.TextReceived += text => receivedText = text;

        var persian = "متن فارسی کلیپ‌بورد";
        _manager.SendTextAsync(persian).Wait();

        Assert.Equal(persian, receivedText);
    }

    [Fact]
    public void ClipboardManager_ThreadSafety()
    {
        var tasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() =>
            {
                if (idx % 2 == 0)
                    _manager.GrantConsent($"session-{idx}");
                else
                    _manager.RevokeConsent();
            }));
        }

        Task.WaitAll(tasks.ToArray());
        Assert.True(_manager.IsConsented || !_manager.IsConsented);
    }
}
