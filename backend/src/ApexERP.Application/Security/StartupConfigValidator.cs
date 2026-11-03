
using System;
using System.Collections.Generic;
using System.Linq;

namespace ApexERP.Application.Security
{
    /// <summary>
    /// The security-critical configuration the API needs before it may start.
    /// </summary>
    public sealed record StartupConfig(
        string? JwtSecret,
        string? JwtIssuer,
        string? ConnectionString,
        string? FrontendOrigins);

    /// <summary>
    /// Fail-fast validation of security-critical configuration.
    /// Outside Development, the application refuses to start unless
    /// a strong, non-placeholder secret is supplied through configuration.
    /// </summary>
    public static class StartupConfigValidator
    {
        public const int MinSecretLength = 32;
        private const int MinDistinctCharacters = 10;

        private static readonly string[] PlaceholderMarkers =
        {
            "change_me",
            "changeme",
            "change-me",
            "dev_only",
            "dev-only",
            "development-only",
            "your_secret",
            "your-secret",
            "secret_key_here",
            "placeholder",
            "example",
            "do-not-use",
            "do_not_use"
        };

        /// <summary>
        /// Returns every configuration problem found.
        /// An empty list means the configuration is acceptable.
        /// </summary>
        public static IReadOnlyList<string> Validate(
            StartupConfig config,
            bool isDevelopment)
        {
            var problems = new List<string>();

            // JWT secret
            var secret = config.JwtSecret;

            if (string.IsNullOrWhiteSpace(secret))
            {
                problems.Add(
                    "Jwt:Secret is not configured. Set the Jwt__Secret environment variable to a long random value " +
                    "(for example: openssl rand -base64 48).");
            }
            else
            {
                if (secret.Length < MinSecretLength)
                {
                    problems.Add(
                        $"Jwt:Secret is too short ({secret.Length} characters); " +
                        $"at least {MinSecretLength} are required.");
                }

                if (!isDevelopment)
                {
                    if (LooksLikePlaceholder(secret))
                    {
                        problems.Add(
                            "Jwt:Secret is a placeholder or development value. " +
                            "Generate a unique production secret.");
                    }

                    if (secret.Distinct().Count() < MinDistinctCharacters)
                    {
                        problems.Add(
                            "Jwt:Secret has too little variety to be a safe signing key. " +
                            "Use a long random value.");
                    }
                }
            }

            // JWT issuer
            if (string.IsNullOrWhiteSpace(config.JwtIssuer))
            {
                problems.Add("Jwt:Issuer is not configured.");
            }

            // Database connection
            if (string.IsNullOrWhiteSpace(config.ConnectionString))
            {
                problems.Add("ConnectionStrings:Default is not configured.");
            }
            else if (!isDevelopment &&
                     config.ConnectionString.Contains(
                         "apexerp_dev_password",
                         StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    "ConnectionStrings:Default uses the development database password. " +
                    "Use a unique production credential.");
            }

            // CORS / frontend origins
            if (!isDevelopment)
            {
                if (string.IsNullOrWhiteSpace(config.FrontendOrigins))
                {
                    problems.Add(
                        "Frontend:Origin is not configured. Set it to the deployed web app's origin " +
                        "(comma-separate several), otherwise browsers cannot call the API.");
                }
                else
                {
                    var origins = SplitOrigins(config.FrontendOrigins).ToArray();

                    if (origins.Length == 0)
                    {
                        problems.Add(
                            "Frontend:Origin must contain at least one valid origin.");
                    }

                    foreach (var origin in origins)
                    {
                        if (!Uri.TryCreate(
                                origin,
                                UriKind.Absolute,
                                out var uri) ||
                            (uri.Scheme != Uri.UriSchemeHttp &&
                             uri.Scheme != Uri.UriSchemeHttps))
                        {
                            problems.Add(
                                $"Frontend:Origin entry '{origin}' is not an absolute http(s) URL.");
                        }
                        else if (uri.IsLoopback)
                        {
                            problems.Add(
                                $"Frontend:Origin entry '{origin}' points at localhost; " +
                                "set the real deployed origin.");
                        }
                    }
                }
            }

            return problems;
        }

        public static IEnumerable<string> SplitOrigins(string? origins) =>
            (origins ?? string.Empty)
                .Split(
                    new[] { ',', ';' },
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Select(origin => origin.TrimEnd('/'));

        public static bool LooksLikePlaceholder(string secret) =>
            PlaceholderMarkers.Any(marker =>
                secret.Contains(
                    marker,
                    StringComparison.OrdinalIgnoreCase));
    }
}
