using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Interaction;
using JetBrains.Annotations;
using SteamKit2;

namespace PostComment;

#pragma warning disable CA1812 // ASF uses this class during runtime
[UsedImplicitly]
internal sealed class PostComment : IASF, IBotCommand2, IGitHubPluginUpdates {
	private const string CommandName = "POSTCOMMENT";
	private const byte MaxCommentLength = 255; // Steam's profile comment length limit

	private static readonly Uri SteamCommunityURL = new("https://steamcommunity.com");

	private bool Enabled;

	public string Name => nameof(PostComment);
	public string RepositoryName => "buddymurdock/ASF-PostComment";
	public Version Version => typeof(PostComment).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads PostCommentEnabled from the global ASF.json config
	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		if (additionalConfigProperties != null) {
			foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
				if ((configProperty == $"{nameof(PostComment)}Enabled") && (configValue.ValueKind is JsonValueKind.True or JsonValueKind.False)) {
					Enabled = configValue.GetBoolean();

					break;
				}
			}
		}

		ASF.ArchiLogger.LogGenericInfo(Enabled ? $"{Name} is enabled, use \"{CommandName} <bots> <steamID> <text>\" to post a comment." : $"{Name} is disabled, set {nameof(PostComment)}Enabled to true in ASF.json to turn it on.");

		return Task.CompletedTask;
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");

		return Task.CompletedTask;
	}

	// ASF calls this for every command it doesn't itself recognize - we only react to our own command name and
	// let everything else fall through untouched (returning null/empty tells ASF this plugin didn't handle it).
	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentException.ThrowIfNullOrEmpty(message);
		ArgumentNullException.ThrowIfNull(args);

		if (!Enabled || (args.Length == 0) || !string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase)) {
			return null;
		}

		bool verbose = access >= EAccess.Owner;

		if (args.Length < 4) {
			return verbose ? bot.Commands.FormatBotResponse($"Usage: {CommandName} <bots> <steamID> <text>") : null;
		}

		string botNames = args[1];
		string targetSteamIDText = args[2];

		// args[] is whitespace-split, which would collapse the comment text's internal spacing - reconstruct
		// it from the raw message instead, splitting into at most 4 pieces so the 4th captures everything
		// after the first three tokens verbatim (multiple spaces, punctuation, etc. all preserved).
		string[] rawParts = message.Split((char[]?) null, 4, StringSplitOptions.RemoveEmptyEntries);
		string text = rawParts.Length == 4 ? rawParts[3].Trim() : "";

		if (string.IsNullOrWhiteSpace(text)) {
			return verbose ? bot.Commands.FormatBotResponse("Comment text cannot be empty.") : null;
		}

		if (text.Length > MaxCommentLength) {
			return verbose ? bot.Commands.FormatBotResponse($"Comment is too long ({text.Length} > {MaxCommentLength} characters, Steam's limit).") : null;
		}

		if (!ulong.TryParse(targetSteamIDText, out ulong targetSteamID) || !new SteamID(targetSteamID).IsIndividualAccount) {
			return verbose ? bot.Commands.FormatBotResponse($"\"{targetSteamIDText}\" is not a valid individual SteamID64.") : null;
		}

		HashSet<Bot>? bots = Bot.GetBots(botNames);

		if ((bots == null) || (bots.Count == 0)) {
			return verbose ? bot.Commands.FormatBotResponse($"Could not find any bots matching \"{botNames}\".") : null;
		}

		IList<string?> results = await Utilities.InParallel(bots.Select(targetBot => ResponsePostComment(targetBot, Commands.GetProxyAccess(targetBot, access, steamID), targetSteamID, text))).ConfigureAwait(false);

		List<string> responses = [.. results.Where(static result => !string.IsNullOrEmpty(result)).Select(static result => result!)];

		return responses.Count > 0 ? string.Join(Environment.NewLine, responses) : (verbose ? Commands.FormatStaticResponse("None of the requested bots executed this command.") : null);
	}

	private static async Task<string?> ResponsePostComment(Bot bot, EAccess access, ulong targetSteamID, string text) {
		if (access < EAccess.Master) {
			return null;
		}

		if (!bot.IsConnectedAndLoggedOn) {
			return bot.Commands.FormatBotResponse("Not connected!");
		}

		bool success = await PostCommentAsync(bot, targetSteamID, text).ConfigureAwait(false);

		return bot.Commands.FormatBotResponse(success ? $"Posted a comment on {targetSteamID}'s wall." : "Failed to post the comment.");
	}

	// Same steamcommunity.com/comment/Profile/post/<id>/-1 endpoint already verified and used by
	// RandomBotComments (github.com/buddymurdock/ASF-RandomBotComments) - reused verbatim here, just
	// targeting an arbitrary operator-supplied SteamID instead of a bot's already-befriended bot.
	private static async Task<bool> PostCommentAsync(Bot bot, ulong receiverSteamID, string comment) {
		Uri request = new(SteamCommunityURL, $"/comment/Profile/post/{receiverSteamID}/-1");

		Dictionary<string, string> data = new(StringComparer.Ordinal) {
			{ "comment", comment },
			{ "count", "1" }
		};

		ArchiSteamFarm.Web.Responses.ObjectResponse<CommentPostResponse>? response = await bot.ArchiWebHandler.UrlPostToJsonObjectWithSession<CommentPostResponse>(request, data: data, referer: SteamCommunityURL).ConfigureAwait(false);

		return response?.Content?.Success ?? false;
	}

	private sealed record CommentPostResponse([property: JsonPropertyName("success")] bool Success, [property: JsonPropertyName("error")] string? Error);
}
#pragma warning restore CA1812
