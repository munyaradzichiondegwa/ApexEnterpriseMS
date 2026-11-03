
using System.Linq;
using ApexERP.Application.Security;
using Xunit;

namespace ApexERP.Tests
{
    public class StartupConfigValidatorTests
    {
        private const string StrongSecret =
            "k7Qp2vX9mR4tZ8cB1nL6wY3hD5sF0jGaE+uVoIqN/xA=";

        private const string GoodConnection =
            "Host=db;Database=apexerp;Username=apexerp;Password=Zr8!unique-prod-credential";

        private static StartupConfig Config(
            string? secret = StrongSecret,
            string? issuer = "ApexERP",
            string? connection = GoodConnection,
            string? origin = "https://erp.example.co.zw") =>
            new(secret, issuer, connection, origin);

        [Fact]
        public void A_complete_production_configuration_is_accepted()
        {
            Assert.Empty(
                StartupConfigValidator.Validate(Config(), isDevelopment: false));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_missing_secret_is_refused_in_every_environment(string? secret)
        {
            Assert.Contains(
                StartupConfigValidator.Validate(
                    Config(secret: secret), isDevelopment: false),
                p => p.Contains("Jwt:Secret"));

            Assert.Contains(
                StartupConfigValidator.Validate(
                    Config(secret: secret), isDevelopment: true),
                p => p.Contains("Jwt:Secret"));
        }

        [Fact]
        public void A_short_secret_is_refused_in_every_environment()
        {
            Assert.Contains(
                StartupConfigValidator.Validate(
                    Config(secret: "short-secret"), isDevelopment: true),
                p => p.Contains("too short"));

            Assert.Contains(
                StartupConfigValidator.Validate(
                    Config(secret: "short-secret"), isDevelopment: false),
                p => p.Contains("too short"));
        }

        [Theory]
        [InlineData("CHANGE_ME_IN_PRODUCTION_this_is_a_dev_only_secret_key_32chars+")]
        [InlineData("CHANGE_ME_IN_PRODUCTION_use_a_long_random_secret_at_least_32_chars")]
        [InlineData("dev-only-local-signing-key-never-use-in-production-9f3b7c1e5a")]
        [InlineData("please-do-not-use-this-example-signing-key-0123456789")]
        public void Known_placeholder_secrets_are_refused_in_production(string secret)
        {
            var problems = StartupConfigValidator.Validate(
                Config(secret: secret), isDevelopment: false);

            Assert.Contains(problems, p => p.Contains("placeholder"));
        }

        [Fact]
        public void A_development_secret_is_allowed_only_in_development()
        {
            const string devSecret =
                "dev-only-local-signing-key-never-use-in-production-9f3b7c1e5a";

            Assert.Empty(
                StartupConfigValidator.Validate(
                    Config(secret: devSecret, origin: null),
                    isDevelopment: true));

            Assert.NotEmpty(
                StartupConfigValidator.Validate(
                    Config(secret: devSecret),
                    isDevelopment: false));
        }

        [Fact]
        public void A_long_but_low_variety_secret_is_refused_in_production()
        {
            var problems = StartupConfigValidator.Validate(
                Config(secret: new string('a', 64)),
                isDevelopment: false);

            Assert.Contains(problems, p => p.Contains("variety"));
        }

        [Fact]
        public void Missing_issuer_and_connection_string_are_refused()
        {
            var problems = StartupConfigValidator.Validate(
                Config(issuer: null, connection: ""),
                isDevelopment: true);

            Assert.Contains(problems, p => p.Contains("Jwt:Issuer"));
            Assert.Contains(problems, p => p.Contains("ConnectionStrings:Default"));
        }

        [Fact]
        public void The_development_database_password_is_refused_in_production_only()
        {
            var conn =
                "Host=localhost;Database=apexerp;Username=apexerp;Password=apexerp_dev_password";

            Assert.Contains(
                StartupConfigValidator.Validate(
                    Config(connection: conn), isDevelopment: false),
                p => p.Contains("development database password"));

            Assert.Empty(
                StartupConfigValidator.Validate(
                    Config(connection: conn, secret: StrongSecret),
                    isDevelopment: true));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("http://localhost:3000")]
        [InlineData("http://127.0.0.1:3000")]
        [InlineData("not-a-url")]
        [InlineData("ftp://erp.example.com")]
        public void A_production_origin_must_be_a_real_http_url(string? origin)
        {
            Assert.Contains(
                StartupConfigValidator.Validate(
                    Config(origin: origin), isDevelopment: false),
                p => p.Contains("Frontend:Origin"));
        }

        [Theory]
        [InlineData(",")]
        [InlineData(",,")]
        [InlineData(";")]
        [InlineData(", ; ,")]
        [InlineData(" ; ; ")]
        public void Separator_only_origins_are_refused_in_production(string origin)
        {
            var problems = StartupConfigValidator.Validate(
                Config(origin: origin),
                isDevelopment: false);

            Assert.Contains(
                problems,
                p => p.Contains("Frontend:Origin"));
        }

        [Fact]
        public void Several_comma_separated_origins_are_accepted_and_split()
        {
            var origins =
                "https://erp.example.co.zw, https://preview-1.vercel.app/ ;https://erp2.example.co.zw";

            Assert.Empty(
                StartupConfigValidator.Validate(
                    Config(origin: origins), isDevelopment: false));

            Assert.Equal(
                new[]
                {
                    "https://erp.example.co.zw",
                    "https://preview-1.vercel.app",
                    "https://erp2.example.co.zw"
                },
                StartupConfigValidator.SplitOrigins(origins).ToArray());
        }

        [Fact]
        public void One_bad_origin_in_a_list_is_reported()
        {
            var problems = StartupConfigValidator.Validate(
                Config(origin: "https://ok.example.com,http://localhost:3000"),
                isDevelopment: false);

            Assert.Single(problems);
        }
    }
}
