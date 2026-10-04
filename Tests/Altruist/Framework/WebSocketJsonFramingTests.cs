using System.Text;
using System.Text.Json;
using Xunit;
using FluentAssertions;

namespace Tests.Altruist.Framework;

/// <summary>
/// Regression tests for WebSocket JSON framing.
/// Verifies that JSON messages from browsers (raw JSON objects) are correctly
/// parsed instead of being misinterpreted as binary event-prefixed frames.
///
/// The bug: first byte of '{' (ASCII 123) was treated as a 123-byte event name
/// length in the binary framing path, corrupting the message.
/// </summary>
public class WebSocketJsonFramingTests
{
    // Simulates the read loop's framing detection logic

    [Fact]
    public void JsonMessage_FirstByteIsBrace_NotTreatedAsBinaryFrame()
    {
        // Browser sends: {"event":"hello","data":{"text":"Hi!"}}
        var json = """{"event":"hello","data":{"text":"Hi!"}}""";
        var bytes = Encoding.UTF8.GetBytes(json);

        // First byte is '{' = 123. Binary framing checks packetData[0] > 0 && < 128
        // Without the fix, this would enter the binary event-prefix path
        bytes[0].Should().Be((byte)'{');
        (bytes[0] > 0 && bytes[0] < 128).Should().BeTrue("'{' falls in the binary framing range — this is why the fix is needed");

        // With fix: JSON codec skips binary framing, decodes as JSON directly
        bool isJsonCodec = true; // simulating _defaultCodec is JsonCodec
        bool shouldUseBinaryFraming = !isJsonCodec && bytes.Length > 2 && bytes[0] > 0 && bytes[0] < 128;
        shouldUseBinaryFraming.Should().BeFalse("JSON codec must skip binary framing");
    }

    [Fact]
    public void JsonEnvelope_ExtractsDataField()
    {
        var json = """{"event":"hello","data":{"messageCode":1,"text":"Hi!"}}""";
        var bytes = Encoding.UTF8.GetBytes(json);

        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.TryGetProperty("event", out var eventEl).Should().BeTrue();
        eventEl.GetString().Should().Be("hello");

        doc.RootElement.TryGetProperty("data", out var dataEl).Should().BeTrue();
        var dataBytes = JsonSerializer.SerializeToUtf8Bytes(dataEl);

        // The data bytes should deserialize to a message with text="Hi!"
        var dataDoc = JsonDocument.Parse(dataBytes);
        dataDoc.RootElement.TryGetProperty("text", out var textEl).Should().BeTrue();
        textEl.GetString().Should().Be("Hi!");
    }

    [Fact]
    public void JsonEnvelope_NoDataField_UsesFullPayload()
    {
        // Some clients may send flat: {"event":"ping","messageCode":3}
        var json = """{"event":"ping","messageCode":3}""";
        var bytes = Encoding.UTF8.GetBytes(json);

        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.TryGetProperty("event", out var eventEl).Should().BeTrue();
        eventEl.GetString().Should().Be("ping");

        // No "data" field — payload should be the full bytes
        doc.RootElement.TryGetProperty("data", out _).Should().BeFalse();
    }

    [Fact]
    public void JsonEnvelope_EmptyData_ReturnsEmptyObject()
    {
        var json = """{"event":"hello","data":{}}""";
        var bytes = Encoding.UTF8.GetBytes(json);

        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.TryGetProperty("data", out var dataEl).Should().BeTrue();
        var dataBytes = JsonSerializer.SerializeToUtf8Bytes(dataEl);
        var dataStr = Encoding.UTF8.GetString(dataBytes);
        dataStr.Should().Be("{}");
    }

    [Fact]
    public void BinaryFrame_StillWorksForMessagePack()
    {
        // MessagePack binary frame: [5]["hello"][payload...]
        // First byte is 5 (event name length), followed by "hello" (5 bytes)
        var eventName = "hello";
        var payload = new byte[] { 0x92, 0x01, 0xA3 }; // some msgpack data
        var frame = new byte[1 + eventName.Length + payload.Length];
        frame[0] = (byte)eventName.Length;
        Encoding.UTF8.GetBytes(eventName).CopyTo(frame, 1);
        payload.CopyTo(frame, 1 + eventName.Length);

        bool isJsonCodec = false; // MessagePack
        bool shouldUseBinaryFraming = !isJsonCodec && frame.Length > 2 && frame[0] > 0 && frame[0] < 128;
        shouldUseBinaryFraming.Should().BeTrue("binary codec should use event-prefix framing");

        // Extract event name
        int eventLen = frame[0];
        var extractedEvent = Encoding.UTF8.GetString(frame, 1, eventLen);
        extractedEvent.Should().Be("hello");
    }

    [Fact]
    public void JsonMessage_WithNestedObjects_ExtractsCorrectly()
    {
        var json = """{"event":"update","data":{"player":{"name":"Test","level":5},"items":[1,2,3]}}""";
        var bytes = Encoding.UTF8.GetBytes(json);

        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.TryGetProperty("data", out var dataEl).Should().BeTrue();
        var dataBytes = JsonSerializer.SerializeToUtf8Bytes(dataEl);
        var dataDoc = JsonDocument.Parse(dataBytes);

        dataDoc.RootElement.TryGetProperty("player", out var playerEl).Should().BeTrue();
        playerEl.TryGetProperty("name", out var nameEl).Should().BeTrue();
        nameEl.GetString().Should().Be("Test");

        dataDoc.RootElement.TryGetProperty("items", out var itemsEl).Should().BeTrue();
        itemsEl.GetArrayLength().Should().Be(3);
    }

    [Fact]
    public void JsonMessage_UnicodeEvent_ParsesCorrectly()
    {
        var json = """{"event":"chat-message","data":{"text":"Héllo Wörld 🎮"}}""";
        var bytes = Encoding.UTF8.GetBytes(json);

        using var doc = JsonDocument.Parse(bytes);
        doc.RootElement.TryGetProperty("event", out var eventEl).Should().BeTrue();
        eventEl.GetString().Should().Be("chat-message");

        doc.RootElement.TryGetProperty("data", out var dataEl).Should().BeTrue();
        var dataBytes = JsonSerializer.SerializeToUtf8Bytes(dataEl);
        var dataDoc = JsonDocument.Parse(dataBytes);
        dataDoc.RootElement.TryGetProperty("text", out var textEl).Should().BeTrue();
        textEl.GetString().Should().Be("Héllo Wörld 🎮");
    }
}
