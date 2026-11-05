using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>
/// The account's extra training queues (Multiple Character Training), as userSvc reads and activates them. Slots are the client's
/// numbers (2 and 3); end times are FILETIME longs.
/// </summary>
public interface IMultipleTrainingCallbacks
{
    /// <summary>The queues that run now, by slot number.</summary>
    ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> GetActiveSlotsAsync(long authenticatedUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Activates the Multiple Character Training certificates among <paramref name="tokens"/>: each one adds 30 days to a queue and is consumed
    /// once. <paramref name="trainingId"/> names the queue to extend; null lets the server choose (a free queue, else the one that ends first).
    /// Returns the queues that run after the activation.
    /// </summary>
    ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> ActivateCertificatesAsync(
        long authenticatedUserId, PyValue tokens, int? trainingId, CancellationToken cancellationToken);

    /// <summary>
    /// Uses one Multiple Character Training certificate item (<c>ActivateMultiTraining(itemID, trainingID)</c>): the item is consumed
    /// and the queue gets 30 days. Refuses an item that is not such a certificate, or that no character of the account holds.
    /// Returns the queues that run after the activation.
    /// </summary>
    ValueTask<IReadOnlyList<(int Slot, long EndsAt)>> ActivateItemAsync(
        long authenticatedUserId, long itemId, int? trainingId, CancellationToken cancellationToken);
}
