namespace Capacitor.Models.Transcripts;

/// Per-stream state a projection keeps between lines. The caller owns one per stream and calls
/// BeginBatch at every batch boundary; what a vendor clears there is the vendor's to define.
public abstract class TranscriptContext {
    public virtual void BeginBatch() { }
}
