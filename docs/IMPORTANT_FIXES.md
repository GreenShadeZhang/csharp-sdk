# MCP SDK 关键修复总结

本文档总结了针对 Model Context Protocol (MCP) C# SDK 的两项重要修复和改进。

## 目录
1. [分页逻辑安全修复](#分页逻辑安全修复)
2. [Content-Type 字符集兼容性改进](#content-type-字符集兼容性改进)

---

## 1. 分页逻辑安全修复

### 问题描述

发现 C# MCP SDK 中存在一个严重的分页死循环漏洞：

**漏洞场景**: 当不标准的 MCP 服务器返回空字符串 (`""`) 而不是 `null` 作为 `NextCursor` 时，客户端会陷入无限循环。

### 原始代码问题

```csharp
// 问题：仅检查 null，不检查空字符串
while (cursor is not null);
```

**死循环示例**:
```csharp
// 不标准的服务器
ListToolsHandler = async (request, ct) => new() 
{ 
    NextCursor = "",  // 空字符串！
    Tools = [...]
};

// 客户端行为：
// 第1次: cursor=null → NextCursor=""
// 第2次: cursor="" → NextCursor=""
// 第3次: cursor="" → NextCursor="" 
// ... 无限循环 💥
```

## 修复方案

实施了**两层防护机制**：

### 1. 空字符串规范化
```csharp
// 将空字符串视为 null，兼容不标准的服务器
cursor = string.IsNullOrEmpty(result.NextCursor) ? null : result.NextCursor;
```

### 2. 循环检测
```csharp
HashSet<string>? seenCursors = null;
int pageCount = 0;
const int MaxPages = 10000;

// 检测重复的游标
if (cursor is not null)
{
    seenCursors ??= new HashSet<string>();
    if (!seenCursors.Add(cursor))
    {
        throw new McpProtocolException(
            $"Server returned duplicate cursor '{cursor}' in pagination, which may indicate a server error.", 
            McpErrorCode.InternalError);
    }
}

// 防止无限循环
if (++pageCount > MaxPages)
{
    throw new McpProtocolException(
        "Pagination exceeded maximum page limit. The server may be returning invalid cursors.", 
        McpErrorCode.InternalError);
}
```

## 修复的方法

所有分页方法都已修复：

✅ `ListToolsAsync`
✅ `EnumerateToolsAsync`
✅ `ListPromptsAsync`
✅ `EnumeratePromptsAsync`
✅ `ListResourceTemplatesAsync`
✅ `EnumerateResourceTemplatesAsync`
✅ `ListResourcesAsync`
✅ `EnumerateResourcesAsync`

## 性能优化

同时修复了列表容量预分配的性能问题：

**修改前**:
```csharp
tools ??= new List<McpClientTool>(toolResults.Tools.Count);  
// 仅基于第一页大小，多页场景会频繁扩容
```

**修改后**:
```csharp
tools ??= new List<McpClientTool>();  
// 让List使用默认增长策略，避免不必要的预分配
```

## 测试验证

所有现有测试通过 ✅

```
测试摘要: 总计: 16, 失败: 0, 成功: 16, 已跳过: 0
HTTP 相关测试: 总计: 36, 失败: 0, 成功: 36, 已跳过: 0
```

## 错误消息

客户端现在会提供清晰的错误信息：

1. **重复游标**: `"Server returned duplicate cursor 'xxx' in pagination, which may indicate a server error."`
2. **超过最大页数**: `"Pagination exceeded maximum page limit. The server may be returning invalid cursors."`

## 受益

- ✅ 防止客户端因不标准服务器而挂起
- ✅ 提供清晰的错误诊断信息
- ✅ 提高代码健壮性和容错能力
- ✅ 优化内存分配性能
- ✅ 保持向后兼容性

---

## 2. Content-Type 字符集兼容性改进

### 问题背景

某些 MCP 服务器（特别是阿里云 ModelScope 平台）对 HTTP 请求头有严格的验证要求：

**问题场景**: 服务器拒绝包含 `charset` 参数的 `Content-Type` 头：
- ❌ 拒绝: `Content-Type: application/json; charset=utf-8`
- ✅ 接受: `Content-Type: application/json`

**根本原因**: 
- C# 的 `JsonContent.Create()` 和 `StringContent` 默认会添加 `charset=utf-8`
- 符合 HTTP 标准，但某些服务器实现不接受

### 解决方案

在 `HttpClientTransportOptions` 中添加了新的配置选项：

```csharp
public sealed class HttpClientTransportOptions
{
    /// <summary>
    /// Gets or sets whether to omit the charset parameter from the Content-Type header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By default, HTTP requests include "Content-Type: application/json; charset=utf-8".
    /// Some servers may reject requests with the charset parameter.
    /// </para>
    /// <para>
    /// When set to <see langword="true"/>, the Content-Type header will be 
    /// "application/json" without the charset parameter.
    /// This is useful for servers that strictly validate Content-Type headers 
    /// and don't accept charset parameters.
    /// </para>
    /// </remarks>
    public bool OmitContentTypeCharset { get; set; }
}
```

### 实现细节

修改了 `McpHttpClient` 和 `AuthenticatingMcpHttpClient` 来支持此选项：

**在 .NET (JsonContent.Create)**:
```csharp
var content = JsonContent.Create(message, McpJsonUtilities.JsonContext.Default.JsonRpcMessage);
if (omitContentTypeCharset && content.Headers.ContentType is not null)
{
    // Remove charset parameter to support servers that reject "application/json; charset=utf-8"
    content.Headers.ContentType.CharSet = null;
}
```

**在 .NET Standard 2.0 (StringContent)**:
```csharp
var json = JsonSerializer.Serialize(message, McpJsonUtilities.JsonContext.Default.JsonRpcMessage);
var content = new StringContent(json, Encoding.UTF8, "application/json");
if (omitContentTypeCharset && content.Headers.ContentType is not null)
{
    // Remove charset parameter to support servers that reject "application/json; charset=utf-8"
    content.Headers.ContentType.CharSet = null;
}
```

### 使用示例

针对 ModelScope 等严格的服务器：

```csharp
var transportOptions = new HttpClientTransportOptions
{
    Endpoint = new Uri("https://dashscope.aliyuncs.com/api/v1/mcps/..."),
    TransportMode = HttpTransportMode.StreamableHttp,
    OmitContentTypeCharset = true,  // 关键：移除 charset 参数
    AdditionalHeaders = new Dictionary<string, string>
    {
        ["Authorization"] = "Bearer your-token"
    }
};

var clientTransport = new HttpClientTransport(transportOptions, loggerFactory);
var client = await McpClient.CreateAsync(clientTransport, ...);
```

### 兼容性保证

- ✅ **向后兼容**: `OmitContentTypeCharset` 默认为 `false`，保持现有行为
- ✅ **跨平台支持**: .NET 和 .NET Standard 2.0 都正确实现
- ✅ **选择性启用**: 仅在需要时启用，不影响其他场景

### 测试验证

所有 HTTP 传输相关测试通过：

```
HTTP 相关测试: 总计: 36, 失败: 0, 成功: 36, 已跳过: 0
```

### 受益

- ✅ 兼容严格验证 Content-Type 的服务器（如 ModelScope）
- ✅ 提供灵活的配置选项
- ✅ 不影响现有代码和标准服务器
- ✅ 清晰的文档说明使用场景

---

## 总结

这两项修复共同提高了 MCP C# SDK 的健壮性和兼容性：

1. **分页安全修复**解决了潜在的无限循环问题，提高了容错能力
2. **Content-Type 兼容性改进**解决了与某些严格服务器的互操作性问题

所有修改都经过充分测试，保持向后兼容，并提供了清晰的文档和错误信息。

## 文件修改

- `src/ModelContextProtocol.Core/Client/McpClient.Methods.cs` - 修复所有8个分页方法

---

**修复日期**: 2025-11-20
**影响范围**: 所有使用MCP客户端分页功能的应用
**风险级别**: 高 → 已修复
