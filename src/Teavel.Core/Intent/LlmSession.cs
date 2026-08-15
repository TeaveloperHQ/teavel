using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Teavel.Intent;

/// <summary>
/// 로컬 모델과의 한 판. 두 가지를 제대로 하기 위해 executor 대신 문맥을 직접 다룬다.
///
/// <b>① 모델의 채팅 형식을 지킨다.</b>
/// Qwen 같은 instruct 모델은 &lt;|im_start|&gt; 로 감싼 형식으로 학습됐다.
/// 평문으로 밀어 넣으면 지시를 훨씬 못 따른다.
///
/// <b>② 안 변하는 앞부분을 다시 읽지 않는다.</b>
/// 도구 목록과 지시문은 매번 똑같은데, 그때마다 수백 토큰을 다시 처리하면
/// GPU 없는 교사 PC 에서 명령 하나에 1분이 넘게 걸린다.
/// 앞부분을 한 번만 처리하고 그 상태를 저장해 두었다가, 매 요청은 저장된 상태에서
/// <b>교사의 말만</b> 이어서 처리한다.
/// </summary>
public sealed class LlmSession : IDisposable
{
    // 프리픽스와 접미부를 가르는 표식. 템플릿을 이 글자와 함께 한 번 렌더링해서
    // '앞부분 / 교사의 말 / 뒷부분' 세 조각으로 안전하게 쪼갠다.
    // (모델마다 다른 채팅 형식을 우리가 직접 알 필요가 없어진다)
    private const string Sentinel = "TEAVEL";

    private static readonly LLamaSeqId Seq = LLamaSeqId.Zero;

    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly string _prefixText;
    private readonly string _suffixText;
    private readonly int _prefixLength;
    private readonly LLamaContext.State _prefixState;

    private bool _disposed;

    /// <param name="weights">이미 적재된 모델.</param>
    /// <param name="parameters">문맥 설정.</param>
    /// <param name="systemPrompt">매번 똑같은 지시문(도구 목록 포함). 이것이 캐시된다.</param>
    public LlmSession(LLamaWeights weights, ModelParams parameters, string systemPrompt)
    {
        _weights = weights;
        _context = weights.CreateContext(parameters);

        // 모델 자신의 채팅 템플릿으로 렌더링한다. 표식 자리에 교사의 말이 들어간다.
        var template = new LLamaTemplate(weights) { AddAssistant = true };
        template.Add("system", systemPrompt);
        template.Add("user", Sentinel);
        var rendered = Encoding.UTF8.GetString(template.Apply());

        var cut = rendered.IndexOf(Sentinel, StringComparison.Ordinal);
        if (cut < 0)
            throw new InvalidOperationException("채팅 템플릿을 나누지 못했습니다.");

        _prefixText = rendered[..cut];
        _suffixText = rendered[(cut + Sentinel.Length)..];

        // 앞부분을 한 번만 처리하고 그 상태를 붙잡아 둔다.
        var prefixTokens = _context.Tokenize(_prefixText, addBos: true, special: true);
        _prefixLength = prefixTokens.Length;

        // 문맥은 RAM 이라 필요한 만큼만 잡아 두었다. 도구를 늘리면 지시문이 길어져
        // 어느 순간 교사의 말이 들어갈 자리가 없어진다 — 그때 조용히 이상해지지 않게 여기서 막는다.
        var room = (int)parameters.ContextSize.GetValueOrDefault(2048);
        if (_prefixLength > room * 3 / 4)
            throw new InvalidOperationException(
                $"지시문이 문맥에 비해 너무 깁니다({_prefixLength} / {room} 토큰). "
              + "도구를 줄이거나 TeavelModelConfig 의 문맥 크기를 올려 주세요.");

        DecodeAll(prefixTokens, startPosition: 0);

        _prefixState = _context.GetState();
    }

    /// <summary>캐시된 앞부분의 토큰 수(속도를 가늠할 때 쓴다).</summary>
    public int CachedTokens => _prefixLength;

    /// <summary>
    /// 토큰 묶음을 한 배치 크기씩 끊어 넣는다.
    /// 도구 목록만으로도 기본 배치 크기를 넘기므로 한 번에 밀어 넣을 수 없다.
    /// </summary>
    /// <returns>마지막 토큰의 logit 자리. 실패하면 -1.</returns>
    private int DecodeAll(IReadOnlyList<LLamaToken> tokens, int startPosition)
    {
        if (tokens.Count == 0) return -1;

        var chunk = Math.Max(1, (int)_context.BatchSize);
        var batch = new LLamaBatch();
        var logitIndex = -1;

        for (var offset = 0; offset < tokens.Count; offset += chunk)
        {
            var take = Math.Min(chunk, tokens.Count - offset);
            batch.Clear();

            for (var i = 0; i < take; i++)
            {
                // logit 은 맨 마지막 토큰에서만 필요하다 — 그 자리에서만 다음 낱말을 고르기 때문이다.
                var isLast = offset + i == tokens.Count - 1;
                var idx = batch.Add(tokens[offset + i], startPosition + offset + i, Seq, logits: isLast);
                if (isLast) logitIndex = idx;
            }

            if (_context.Decode(batch) != DecodeResult.Ok) return -1;
        }

        return logitIndex;
    }

    /// <summary>교사의 말을 넣고 모델의 답을 받는다.</summary>
    /// <param name="userText">교사가 한 말.</param>
    /// <param name="maxTokens">최대 생성 길이.</param>
    /// <param name="stopAt">이 글자가 나오면 멈춘다.</param>
    public async Task<string> AskAsync(
        string userText, int maxTokens, IReadOnlyList<string>? stopAt = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 앞부분까지 처리된 상태로 되돌린다. 지난 요청의 흔적이 남지 않는다.
            _context.LoadState(_prefixState);

            var tail = _context.Tokenize(userText + _suffixText, addBos: false, special: true);

            var logitIndex = DecodeAll(tail, startPosition: _prefixLength);
            if (logitIndex < 0) return "";

            var position = _prefixLength + tail.Length;
            var batch = new LLamaBatch();

            using var sampler = new GreedySamplingPipeline();

            // 한글은 한 글자가 여러 토큰에 걸쳐 있어, 토큰마다 따로 글자로 바꾸면 깨진다.
            // StreamingTokenDecoder 가 바이트가 모일 때까지 붙들고 있다가 온전한 글자로 내준다.
            var decoder = new StreamingTokenDecoder(_context);
            var text = new StringBuilder();

            for (var n = 0; n < maxTokens; n++)
            {
                ct.ThrowIfCancellationRequested();

                var token = sampler.Sample(_context.NativeHandle, logitIndex);
                if (_weights.Vocab.EOS == token || _weights.Vocab.EOT == token) break;

                decoder.Add(token);
                text.Append(decoder.Read());

                if (stopAt is not null)
                {
                    var current = text.ToString();
                    var hit = stopAt.FirstOrDefault(s =>
                        s.Length > 0 && current.Contains(s, StringComparison.Ordinal));
                    if (hit is not null)
                        return current[..current.IndexOf(hit, StringComparison.Ordinal)].Trim();
                }

                batch.Clear();
                logitIndex = batch.Add(token, position++, Seq, logits: true);

                if (await _context.DecodeAsync(batch, ct).ConfigureAwait(false) != DecodeResult.Ok) break;
            }

            return text.ToString().Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _prefixState.Dispose();
        _context.Dispose();
        _gate.Dispose();
    }
}
