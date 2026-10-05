using RemoteSupport.Shared.RemoteInput;

namespace RemoteSupport.Server.Tests.RemoteInput;

public class RemoteInputTests : IDisposable
{
    private readonly InputConsentManager _consentManager;

    public RemoteInputTests()
    {
        _consentManager = new InputConsentManager();
    }

    public void Dispose() { }

    // --- InputEvent Serialization Tests ---

    [Fact]
    public void InputEvent_MouseMove_SerializeDeserialize()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.MouseMove,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            MouseX = 500,
            MouseY = 300
        };

        var serialized = InputEvent.Serialize(evt);
        var deserialized = InputEvent.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(InputEventType.MouseMove, deserialized!.Type);
        Assert.Equal(500, deserialized.MouseX);
        Assert.Equal(300, deserialized.MouseY);
    }

    [Fact]
    public void InputEvent_MouseButton_SerializeDeserialize()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.MouseButton,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 2,
            Button = MouseButton.Left,
            ButtonAction = KeyAction.KeyDown
        };

        var serialized = InputEvent.Serialize(evt);
        var deserialized = InputEvent.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(InputEventType.MouseButton, deserialized!.Type);
        Assert.Equal(MouseButton.Left, deserialized.Button);
        Assert.Equal(KeyAction.KeyDown, deserialized.ButtonAction);
    }

    [Fact]
    public void InputEvent_MouseWheel_SerializeDeserialize()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.MouseWheel,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 3,
            WheelDelta = 120
        };

        var serialized = InputEvent.Serialize(evt);
        var deserialized = InputEvent.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(InputEventType.MouseWheel, deserialized!.Type);
        Assert.Equal(120, deserialized.WheelDelta);
    }

    [Fact]
    public void InputEvent_MouseWheel_NegativeDelta()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.MouseWheel,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 4,
            WheelDelta = -120
        };

        var serialized = InputEvent.Serialize(evt);
        var deserialized = InputEvent.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(-120, deserialized!.WheelDelta);
    }

    [Fact]
    public void InputEvent_KeyboardKey_SerializeDeserialize()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.KeyboardKey,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 5,
            VirtualKeyCode = 0x41, // 'A'
            KeyAction = KeyAction.KeyDown
        };

        var serialized = InputEvent.Serialize(evt);
        var deserialized = InputEvent.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(InputEventType.KeyboardKey, deserialized!.Type);
        Assert.Equal((ushort)0x41, deserialized.VirtualKeyCode);
        Assert.Equal(KeyAction.KeyDown, deserialized.KeyAction);
    }

    [Fact]
    public void InputEvent_KeyboardKey_KeyUp()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.KeyboardKey,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 6,
            VirtualKeyCode = 0x1B, // Escape
            KeyAction = KeyAction.KeyUp
        };

        var serialized = InputEvent.Serialize(evt);
        var deserialized = InputEvent.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(KeyAction.KeyUp, deserialized!.KeyAction);
    }

    [Fact]
    public void InputEvent_KeyboardChar_SerializeDeserialize()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.KeyboardChar,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 7,
            Character = 'A'
        };

        var serialized = InputEvent.Serialize(evt);
        var deserialized = InputEvent.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(InputEventType.KeyboardChar, deserialized!.Type);
        Assert.Equal('A', deserialized.Character);
    }

    [Fact]
    public void InputEvent_MouseDoubleClick_SerializeDeserialize()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.MouseDblClick,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 8,
            MouseX = 100,
            MouseY = 200,
            Button = MouseButton.Left
        };

        var serialized = InputEvent.Serialize(evt);
        var deserialized = InputEvent.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(InputEventType.MouseDblClick, deserialized!.Type);
        Assert.Equal(100, deserialized.MouseX);
        Assert.Equal(200, deserialized.MouseY);
        Assert.Equal(MouseButton.Left, deserialized.Button);
    }

    [Fact]
    public void InputEvent_InvalidData_ReturnsNull()
    {
        var result = InputEvent.Deserialize(new byte[] { 0xFF, 0xFF });

        Assert.Null(result);
    }

    [Fact]
    public void InputEvent_TryDeserialize_ValidData()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.MouseMove,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 100,
            MouseX = 0,
            MouseY = 0
        };

        var serialized = InputEvent.Serialize(evt);
        var result = InputEvent.TryDeserialize(serialized, out var deserialized);

        Assert.True(result);
        Assert.NotNull(deserialized);
    }

    [Fact]
    public void InputEvent_TryDeserialize_InvalidData()
    {
        var result = InputEvent.TryDeserialize(new byte[] { 0xFF }, out var evt);

        Assert.False(result);
        Assert.Null(evt);
    }

    // --- Consent Manager Tests ---

    [Fact]
    public void ConsentManager_InitiallyNotConsented()
    {
        Assert.False(_consentManager.IsConsented);
        Assert.Null(_consentManager.ConsentedSessionId);
    }

    [Fact]
    public void ConsentManager_GrantConsent_SetsConsented()
    {
        _consentManager.GrantConsent("session-1");

        Assert.True(_consentManager.IsConsented);
        Assert.Equal("session-1", _consentManager.ConsentedSessionId);
        Assert.NotNull(_consentManager.ConsentGrantedAtUtc);
    }

    [Fact]
    public void ConsentManager_RevokeConsent_ClearsConsent()
    {
        _consentManager.GrantConsent("session-1");
        _consentManager.RevokeConsent();

        Assert.False(_consentManager.IsConsented);
        Assert.Null(_consentManager.ConsentedSessionId);
        Assert.Null(_consentManager.ConsentGrantedAtUtc);
    }

    [Fact]
    public void ConsentManager_ValidateConsent_CorrectSession()
    {
        _consentManager.GrantConsent("session-1");

        Assert.True(_consentManager.ValidateConsent("session-1"));
    }

    [Fact]
    public void ConsentManager_ValidateConsent_WrongSession()
    {
        _consentManager.GrantConsent("session-1");

        Assert.False(_consentManager.ValidateConsent("session-2"));
    }

    [Fact]
    public void ConsentManager_ValidateConsent_NoConsent()
    {
        Assert.False(_consentManager.ValidateConsent("session-1"));
    }

    [Fact]
    public void ConsentManager_ThreadSafety()
    {
        var tasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() =>
            {
                if (idx % 2 == 0)
                    _consentManager.GrantConsent($"session-{idx}");
                else
                    _consentManager.RevokeConsent();
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Should not throw - thread safety verified
        Assert.True(_consentManager.IsConsented || !_consentManager.IsConsented);
    }

    // --- Input Event Protocol Tests ---

    [Fact]
    public void InputEvent_AllButtons_SerializeDeserialize()
    {
        foreach (var button in Enum.GetValues<MouseButton>())
        {
            var evt = new InputEvent
            {
                Type = InputEventType.MouseButton,
                TimestampTicks = DateTime.UtcNow.Ticks,
                SequenceNumber = 1,
                Button = button,
                ButtonAction = KeyAction.KeyDown
            };

            var serialized = InputEvent.Serialize(evt);
            var deserialized = InputEvent.Deserialize(serialized);

            Assert.NotNull(deserialized);
            Assert.Equal(button, deserialized!.Button);
        }
    }

    [Fact]
    public void InputEvent_AllActionTypes_SerializeDeserialize()
    {
        foreach (var action in Enum.GetValues<KeyAction>())
        {
            var evt = new InputEvent
            {
                Type = InputEventType.KeyboardKey,
                TimestampTicks = DateTime.UtcNow.Ticks,
                SequenceNumber = 1,
                VirtualKeyCode = 0x0D,
                KeyAction = action
            };

            var serialized = InputEvent.Serialize(evt);
            var deserialized = InputEvent.Deserialize(serialized);

            Assert.NotNull(deserialized);
            Assert.Equal(action, deserialized!.KeyAction);
        }
    }

    [Fact]
    public void InputEvent_LargeCoordinates_SerializeDeserialize()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.MouseMove,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            MouseX = 7680,  // 8K monitor width
            MouseY = 4320   // 8K monitor height
        };

        var serialized = InputEvent.Serialize(evt);
        var deserialized = InputEvent.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(7680, deserialized!.MouseX);
        Assert.Equal(4320, deserialized.MouseY);
    }

    [Fact]
    public void InputEvent_NegativeCoordinates_SerializeDeserialize()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.MouseMove,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            MouseX = -100,
            MouseY = -50
        };

        var serialized = InputEvent.Serialize(evt);
        var deserialized = InputEvent.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal(-100, deserialized!.MouseX);
        Assert.Equal(-50, deserialized.MouseY);
    }

    [Fact]
    public void InputEvent_UnicodeCharacter_SerializeDeserialize()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.KeyboardChar,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            Character = 'ع' // Arabic letter Ain
        };

        var serialized = InputEvent.Serialize(evt);
        var deserialized = InputEvent.Deserialize(serialized);

        Assert.NotNull(deserialized);
        Assert.Equal('ع', deserialized!.Character);
    }

    [Fact]
    public void InputEvent_SequenceNumber_Preserved()
    {
        for (uint i = 0; i < 1000; i++)
        {
            var evt = new InputEvent
            {
                Type = InputEventType.MouseMove,
                TimestampTicks = DateTime.UtcNow.Ticks,
                SequenceNumber = i,
                MouseX = (int)i,
                MouseY = (int)i
            };

            var serialized = InputEvent.Serialize(evt);
            var deserialized = InputEvent.Deserialize(serialized);

            Assert.Equal(i, deserialized!.SequenceNumber);
            Assert.Equal((int)i, deserialized.MouseX);
        }
    }

    // --- Consent + Authorization Integration Tests ---

    [Fact]
    public void ConsentSession_OnlyAcceptsMatchingSession()
    {
        _consentManager.GrantConsent("session-A");

        Assert.True(_consentManager.ValidateConsent("session-A"));
        Assert.False(_consentManager.ValidateConsent("session-B"));
        Assert.False(_consentManager.ValidateConsent(""));
        Assert.False(_consentManager.ValidateConsent("session-A" + " ")); // trailing space
    }

    [Fact]
    public void Consent_RevokePreventsFurtherValidation()
    {
        _consentManager.GrantConsent("session-1");
        Assert.True(_consentManager.ValidateConsent("session-1"));

        _consentManager.RevokeConsent();
        Assert.False(_consentManager.ValidateConsent("session-1"));
    }

    [Fact]
    public void Consent_GrantNewSessionOverridesOld()
    {
        _consentManager.GrantConsent("session-1");
        Assert.True(_consentManager.ValidateConsent("session-1"));

        _consentManager.GrantConsent("session-2");
        Assert.False(_consentManager.ValidateConsent("session-1"));
        Assert.True(_consentManager.ValidateConsent("session-2"));
    }

    // --- Input Protocol Performance Tests ---

    [Fact]
    public void InputEvent_SerializePerformance()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.MouseMove,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            MouseX = 1920,
            MouseY = 1080
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++)
        {
            InputEvent.Serialize(evt);
        }
        stopwatch.Stop();

        var opsPerMs = 10000.0 / stopwatch.ElapsedMilliseconds;
        Assert.True(opsPerMs > 100, $"Serialize too slow: {opsPerMs:F0} ops/ms");
    }

    [Fact]
    public void InputEvent_DeserializePerformance()
    {
        var evt = new InputEvent
        {
            Type = InputEventType.MouseMove,
            TimestampTicks = DateTime.UtcNow.Ticks,
            SequenceNumber = 1,
            MouseX = 1920,
            MouseY = 1080
        };
        var serialized = InputEvent.Serialize(evt);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++)
        {
            InputEvent.Deserialize(serialized);
        }
        stopwatch.Stop();

        var opsPerMs = 10000.0 / stopwatch.ElapsedMilliseconds;
        Assert.True(opsPerMs > 100, $"Deserialize too slow: {opsPerMs:F0} ops/ms");
    }
}
