using Eve.Marshal;
using Eve.Skills;

namespace Eve.Services.Skills;

/// <summary>What the client shows when a queue is refused: the names of its messages.</summary>
public static class SkillErrors
{
    public static MachoWrappedException Of(QueueError error) => error switch
    {
        QueueError.TooManySkills => MachoErrors.ThrowUserError("QueueTooManySkills", ("num", new PyInt(Training.MaxQueueLength))),
        QueueError.TooLong => MachoErrors.ThrowUserError("QueueTooLong"),
        QueueError.SkillNotUploaded => MachoErrors.ThrowUserError("QueueSkillNotUploaded"),
        QueueError.PastMaximumLevel => MachoErrors.ThrowUserError("QueueCannotTrainPastMaximumLevel"),
        QueueError.PreviouslyTrained => MachoErrors.ThrowUserError("QueueCannotTrainPreviouslyTrainedSkills"),
        QueueError.OutOfOrder => MachoErrors.ThrowUserError("QueueCannotPlaceSkillLevelsOutOfOrder"),
        QueueError.BeforeRequirements => MachoErrors.ThrowUserError("QueueCannotPlaceSkillBeforeRequirements"),
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "a queue error the client has no message for"),
    };

    /// <summary>Another character of the account is training and the account trains one at a time.</summary>
    public static MachoWrappedException Busy(TrainingBusy busy) =>
        MachoErrors.ThrowUserError("UserAlreadyHasSkillInTraining", ("charactersTrainingCount", new PyInt(busy.Training)));

    /// <summary>The error for an outcome that was not a saved queue.</summary>
    public static MachoWrappedException Of(SaveOutcome outcome) => outcome switch
    {
        QueueRefused refused => Of(refused.Error),
        TrainingBusy busy => Busy(busy),
        _ => throw new ArgumentException("the outcome is not a refusal", nameof(outcome)),
    };
}
