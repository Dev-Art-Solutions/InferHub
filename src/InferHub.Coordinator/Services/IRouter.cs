namespace InferHub.Coordinator.Services;

public interface IRouter
{
    /// <summary>
    /// Picks a node for a model. <paramref name="capability"/> (phase 40) narrows the candidates
    /// to nodes that declare they can do this *kind* of work with that model; null keeps the
    /// pre-v3.8 question, "who holds it".
    /// </summary>
    /// <param name="requireStreamedAttachments">
    /// Phase 53, D5. A streamed upload needs a node that can pull one; a fleet with none answers
    /// the phase-40 D4 shape (503 + Retry-After) rather than falling back to the buffered path,
    /// which would work brilliantly right up to the 25 MB it cannot do.
    /// </param>
    /// <param name="requireStreamedSpeech">
    /// Phase 70, D7. Same shape one route over: a node that would answer a streaming synthesis
    /// with a file is not a candidate for one, and a fleet of them answers with the 503 that names
    /// the version rather than with a sentence about attachments.
    /// </param>
    RoutableNode? Route(
        string model,
        string? conversationKey = null,
        string? excludeConnectionId = null,
        string? capability = null,
        bool requireStreamedAttachments = false,
        bool requireStreamedSpeech = false);
}
