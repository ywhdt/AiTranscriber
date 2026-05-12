using System.Globalization;
using System.Text;

namespace WindowsAiTranscriber.Services;

public static class TranscriptionTextCleaner
{
	private const char ReplacementCharacter = '\uFFFD';

	private static readonly char[] StrongMojibakeMarkers =
	[
		'Ã',
		'Â',
		'â',
		'ã',
		'ä',
		'å',
		'æ'
	];

	private static readonly char[] NonSpeechSymbols =
	[
		'♪',
		'♫',
		'♬',
		'♩'
	];

	public static string CleanPartial(string? text)
	{
		return Clean(text, suppressIncompleteMojibake: true);
	}

	public static string CleanCompleted(string? text)
	{
		return Clean(text, suppressIncompleteMojibake: false);
	}

	private static string Clean(string? text, bool suppressIncompleteMojibake)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}

		var repaired = RepairLikelyUtf8Mojibake(text, suppressIncompleteMojibake);
		if (repaired is null)
		{
			return "";
		}

		return RemoveUnsafeCharacters(NormalizeSafely(repaired));
	}

	private static string? RepairLikelyUtf8Mojibake(string text, bool suppressIncompleteMojibake)
	{
		if (!LooksLikeUtf8Mojibake(text))
		{
			return text;
		}

		if (ContainsNonLatin1Character(text))
		{
			return suppressIncompleteMojibake && IsSuspiciousMojibakeFragment(text) ? null : text;
		}

		var bytes = new byte[text.Length];
		for (var i = 0; i < text.Length; i++)
		{
			bytes[i] = (byte)text[i];
		}

		try
		{
			var decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
			if (LooksBetterAfterRepair(text, decoded))
			{
				return decoded;
			}
		}
		catch (DecoderFallbackException)
		{
			return suppressIncompleteMojibake ? null : text;
		}

		return suppressIncompleteMojibake && IsSuspiciousMojibakeFragment(text) ? null : text;
	}

	private static string NormalizeSafely(string text)
	{
		try
		{
			return text.Normalize(NormalizationForm.FormKC);
		}
		catch (ArgumentException)
		{
			return text;
		}
	}

	private static string RemoveUnsafeCharacters(string text)
	{
		var builder = new StringBuilder(text.Length);
		foreach (var ch in text)
		{
			if (ch == ReplacementCharacter || NonSpeechSymbols.Contains(ch))
			{
				continue;
			}

			if (char.IsWhiteSpace(ch))
			{
				builder.Append(' ');
				continue;
			}

			switch (CharUnicodeInfo.GetUnicodeCategory(ch))
			{
				case UnicodeCategory.Control:
				case UnicodeCategory.Format:
				case UnicodeCategory.Surrogate:
				case UnicodeCategory.PrivateUse:
				case UnicodeCategory.OtherNotAssigned:
					continue;
				default:
					builder.Append(ch);
					break;
			}
		}

		return builder.ToString();
	}

	private static bool LooksLikeUtf8Mojibake(string text)
	{
		var strongMarkers = 0;
		var weakMarkers = 0;

		foreach (var ch in text)
		{
			if (StrongMojibakeMarkers.Contains(ch))
			{
				strongMarkers++;
			}

			if ((ch >= '\u0080' && ch <= '\u00FF') || ch == ReplacementCharacter)
			{
				weakMarkers++;
			}
		}

		return strongMarkers >= 2 || strongMarkers >= 1 && weakMarkers >= 2;
	}

	private static bool IsSuspiciousMojibakeFragment(string text)
	{
		if (ContainsEastAsianText(text))
		{
			return false;
		}

		var markers = 0;
		foreach (var ch in text)
		{
			if (StrongMojibakeMarkers.Contains(ch) ||
				ch == ReplacementCharacter ||
				ch is >= '\u0080' and <= '\u009F')
			{
				markers++;
			}
		}

		return markers >= 2;
	}

	private static bool LooksBetterAfterRepair(string original, string decoded)
	{
		if (string.IsNullOrWhiteSpace(decoded) || decoded.Contains(ReplacementCharacter))
		{
			return false;
		}

		if (ContainsEastAsianText(decoded))
		{
			return true;
		}

		return original.Contains('Ã') || original.Contains('Â') || original.Contains('â');
	}

	private static bool ContainsNonLatin1Character(string text)
	{
		foreach (var ch in text)
		{
			if (ch > '\u00FF')
			{
				return true;
			}
		}

		return false;
	}

	private static bool ContainsEastAsianText(string text)
	{
		foreach (var ch in text)
		{
			if (ch is >= '\u3040' and <= '\u30FF' ||
				ch is >= '\u3400' and <= '\u9FFF' ||
				ch is >= '\uAC00' and <= '\uD7AF')
			{
				return true;
			}
		}

		return false;
	}
}
