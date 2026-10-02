using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using XAssistant.Models;
using XAssistant.Services.Interfaces;

namespace XAssistant.Services;

/// <summary>鼠标点击的后台批量写入缓冲，注入 <see cref="BatchWriteBuffer{T}"/>。</summary>
public sealed class MouseClickBuffer : IMouseClickBuffer, IDisposable
{
    private readonly BatchWriteBuffer<MouseClickRecord> _inner;

    public MouseClickBuffer(IClickDatabaseService db, ILogger<MouseClickBuffer> logger)
    {
        _inner = new BatchWriteBuffer<MouseClickRecord>(
            db.SaveClickBatch,
            logger,
            "点击"
        );
    }

    public void Enqueue(MouseClickRecord record) => _inner.Enqueue(record);

    public void Start() => _inner.Start();

    public Task FlushAsync(TimeSpan timeout) => _inner.FlushAsync(timeout);

    public void Dispose() => _inner.Dispose();
}
