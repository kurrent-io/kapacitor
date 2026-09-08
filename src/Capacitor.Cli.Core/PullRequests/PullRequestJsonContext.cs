using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.PullRequests;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PullRequestEnvelopeDto<PullRequestLinkListDto>))]
[JsonSerializable(typeof(PullRequestEnvelopeDto<PullRequestOverviewDto>))]
[JsonSerializable(typeof(PullRequestEnvelopeDto<PullRequestPageDto<PullRequestCheckDto>>))]
[JsonSerializable(typeof(PullRequestEnvelopeDto<PullRequestPageDto<PullRequestReviewerDto>>))]
[JsonSerializable(typeof(PullRequestEnvelopeDto<PullRequestPageDto<PullRequestReviewDto>>))]
[JsonSerializable(typeof(PullRequestEnvelopeDto<PullRequestPageDto<PullRequestThreadDto>>))]
[JsonSerializable(typeof(PullRequestEnvelopeDto<PullRequestPageDto<PullRequestCommentDto>>))]
[JsonSerializable(typeof(PullRequestEnvelopeDto<PullRequestPageDto<object>>))]
[JsonSerializable(typeof(PullRequestEnvelopeDto<object>))]
[JsonSerializable(typeof(PullRequestErrorDto))]
public partial class PullRequestJsonContext : JsonSerializerContext;
