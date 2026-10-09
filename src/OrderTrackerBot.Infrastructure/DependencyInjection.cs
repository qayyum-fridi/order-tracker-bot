using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Ai;
using OrderTrackerBot.Application.Conversation;
using OrderTrackerBot.Infrastructure.Ai;
using OrderTrackerBot.Infrastructure.Alerts;
using OrderTrackerBot.Infrastructure.Backup;
using OrderTrackerBot.Infrastructure.Catalog;
using OrderTrackerBot.Infrastructure.Instagram;
using OrderTrackerBot.Infrastructure.Persistence;
using OrderTrackerBot.Infrastructure.WhatsApp;

namespace OrderTrackerBot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        var provider = configuration["Database:Provider"] ?? "SqlServer";
        services.AddDbContext<AppDbContext>(options =>
        {
            if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
                options.UseSqlite(connectionString);
            else
                options.UseSqlServer(connectionString);
        });
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        // Google Drive backups: Sqlite only, and only when switched on with complete credentials (see README "Backups").
        services.Configure<BackupOptions>(configuration.GetSection(BackupOptions.SectionName));
        var backup = configuration.GetSection(BackupOptions.SectionName).Get<BackupOptions>() ?? new BackupOptions();
        if (backup.Enabled && backup.IsConfigured && provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<IBackupStorage, GoogleDriveBackupStorage>(c => c.Timeout = TimeSpan.FromMinutes(5));
            services.AddTransient<SqliteBackupManager>();
        }

        services.Configure<WhatsAppOptions>(configuration.GetSection(WhatsAppOptions.SectionName));
        services.Configure<WhatsAppTemplatesOptions>(configuration.GetSection(WhatsAppTemplatesOptions.SectionName));
        services.Configure<OpenAiOptions>(configuration.GetSection(OpenAiOptions.SectionName));
        services.Configure<FounderAlertOptions>(configuration.GetSection(FounderAlertOptions.SectionName));
        services.Configure<InstagramOptions>(configuration.GetSection(InstagramOptions.SectionName));

        services.AddHttpClient<IWhatsAppSender, WhatsAppSender>();
        services.AddHttpClient<IAiOrderAssistant, OpenAiOrderAssistant>();
        services.AddHttpClient<IAudioTranscriber, OpenAiAudioTranscriber>();
        services.AddHttpClient<IFounderAlertNotifier, FounderAlertNotifier>();
        services.AddHttpClient<IIssueReporter, IssueReporter>(c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddHttpClient<IWhatsAppMediaClient, WhatsAppMediaClient>();
        services.AddHttpClient<ICatalogSheetImporter, CatalogSheetImporter>();
        services.AddHttpClient<InstagramClient>();
        services.AddScoped<IInstagramClient>(sp => sp.GetRequiredService<InstagramClient>());
        services.AddSingleton(configuration.GetSection(BillingOptions.SectionName).Get<BillingOptions>() ?? new BillingOptions());
        services.AddSingleton(configuration.GetSection(FeatureOptions.SectionName).Get<FeatureOptions>() ?? new FeatureOptions());

        services.AddSingleton<IReceiptPdfGenerator, Pdf.ReceiptPdfGenerator>();
        services.AddSingleton<WhatsApp.WebhookMessageGate>();
        services.AddSingleton<IExportFileWriter, Export.ExportXlsxWriter>();
        services.AddSingleton<IImportFileReader, Export.ImportXlsxReader>();
        services.AddScoped<ConversationEngine>();

        return services;
    }
}
