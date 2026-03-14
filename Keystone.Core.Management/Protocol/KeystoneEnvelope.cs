/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// KeystoneEnvelope — Unified message envelope for all IPC lanes.
// Defined now as the canonical type. Current NDJSON messages will migrate
// to this format in Phase 2. Binary framing uses this as the logical model.

using System.Text.Json.Serialization;

namespace Keystone.Core.Management.Protocol;

/// <summary>
/// The canonical Keystone IPC message envelope.
/// Every IPC lane (stdin/stdout NDJSON, WebSocket JSON, binary frames)
/// maps onto this logical model regardless of wire encoding.
/// </summary>
public sealed class KeystoneEnvelope
{
    /// <summary>Protocol version. Always 1.</summary>
    [JsonPropertyName("v")]
    public int V { get; init; } = 1;

    /// <summary>Message kind — determines which fields are meaningful.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    /// <summary>Request/response correlation ID (connection-local).</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Id { get; init; }

    /// <summary>Stream correlation ID for chunked transfers.</summary>
    [JsonPropertyName("streamId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StreamId { get; init; }

    /// <summary>Operation name — service method, channel, action string.</summary>
    [JsonPropertyName("op")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Op { get; init; }

    /// <summary>Source process/plane identifier.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    /// <summary>Target process/plane identifier.</summary>
    [JsonPropertyName("target")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; init; }

    /// <summary>Window scope for targeted messages.</summary>
    [JsonPropertyName("windowId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowId { get; init; }

    /// <summary>Required capability for the recipient to process this message.</summary>
    [JsonPropertyName("capability")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Capability { get; init; }

    /// <summary>Request deadline in milliseconds (relative to send time).</summary>
    [JsonPropertyName("deadlineMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DeadlineMs { get; init; }

    /// <summary>Payload encoding hint.</summary>
    [JsonPropertyName("encoding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Encoding { get; init; }

    /// <summary>Message payload — type depends on kind and op.</summary>
    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Payload { get; init; }

    /// <summary>Error information (kind="error" or kind="response" with error).</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EnvelopeError? Error { get; init; }
}

/// <summary>Structured error within an envelope.</summary>
public sealed record EnvelopeError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Details = null,
    [property: JsonPropertyName("stack")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Stack = null
);

/// <summary>Well-known envelope kind constants.</summary>
public static class EnvelopeKind
{
    public const string Hello = "hello";
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";
    public const string Cancel = "cancel";
    public const string StreamOpen = "stream_open";
    public const string StreamChunk = "stream_chunk";
    public const string StreamClose = "stream_close";
    public const string Error = "error";
}
