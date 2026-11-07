using Eve.Data;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Persistence;

/// <summary>Durable support inbox for the client's in-game report RPCs.</summary>
public sealed class PersistenceInGameReportStore(NpgsqlDataSource source, string schema = PersistenceInGameReportStore.DefaultSchema)
{
    public const string DefaultSchema = "support";

    private string Q(string sql) => Sql.In(Sql.CheckSchema(schema), sql);

    public static Task MigrateAsync(NpgsqlDataSource source, string schema = DefaultSchema, CancellationToken cancellationToken = default) =>
        Migrations.ApplyAsync(source, schema,
        [new Migration("0001_ingame_reports", """
            CREATE TABLE ingame_reports (
                report_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                report_type text NOT NULL CHECK (report_type IN (
                    'inappropriate_language', 'offensive_mail', 'macro_use', 'questionable_transaction',
                    'inappropriate_character_name', 'exploit_abuse', 'gambling', 'other')),
                method_name text NOT NULL CHECK (method_name IN (
                    'report_inappropriate_language', 'report_offensive_mail', 'report_macro_use',
                    'report_questionable_transaction', 'report_inappropriate_character_name',
                    'report_exploit_abuse', 'report_gambling', 'report_other')),
                reporter_character_id bigint NOT NULL CHECK (reporter_character_id > 0),
                reporter_account_id bigint NULL CHECK (reporter_account_id IS NULL OR reporter_account_id > 0),
                target_character_id bigint NOT NULL CHECK (target_character_id > 0),
                comments text NOT NULL,
                chat_time text NULL,
                chat_channel text NULL,
                chat_content text NULL,
                received_at timestamptz NOT NULL DEFAULT clock_timestamp()
            );
            CREATE INDEX ingame_reports_received_at_idx ON ingame_reports (received_at DESC, report_id DESC);
            """)], cancellationToken);

    /// <summary>Stores the report once before the service acknowledges the client's submission.</summary>
    public async ValueTask SubmitAsync(InGameReportSubmission report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.ReporterCharacterId <= 0 || report.ReporterAccountId is <= 0 || report.TargetCharacterId <= 0 ||
            string.IsNullOrWhiteSpace(report.MethodName) || string.IsNullOrWhiteSpace(report.ReportType))
            throw new ArgumentException("Invalid in-game report identity or type.", nameof(report));

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = Sql.Command(connection, null, Q("""
            INSERT INTO {schema}.ingame_reports (
                report_type, method_name, reporter_character_id, reporter_account_id, target_character_id,
                comments, chat_time, chat_channel, chat_content)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """),
            report.ReportType, report.MethodName, report.ReporterCharacterId, report.ReporterAccountId,
            report.TargetCharacterId, report.Comments, report.ChatTime, report.ChatChannel, report.ChatContent);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
