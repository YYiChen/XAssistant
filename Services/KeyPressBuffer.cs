using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

/// <summary>按键记录的后台批量写入缓冲，注入 <see cref="BatchWriteBuffer{T}"/>。</summary>
public sealed class KeyPressBuffer : IKeyPressBuffer, IDisposable
{
    private readonly BatchWriteBuffer<KeyPressRecord> _inner;

    public KeyPressBuffer(IKeyDatabaseService db, ILogger<KeyPressBuffer> logger)
    {
        _inner = new BatchWriteBuffer<KeyPressRecord>(
            db.SaveKeyPressBatch,
            logger,
            "按键"
        );
    }

    public void Enqueue(KeyPressRecord record) => _inner.Enqueue(record);

    public void Start() => _inner.Start();

    public Task FlushAsync(TimeSpan timeout) => _inner.FlushAsync(timeout);

    public void Dispose() => _inner.Dispose();
}
