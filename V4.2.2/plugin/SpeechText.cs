// Strips roleplay stage directions out of a reply before it reaches TTS.
//
// Models answering in character write actions inline: "*frantically mashing
// buttons* I've almost got it!". The subtitle should keep that - it is half the
// performance - but a voice reading "asterisk frantically mashing buttons
// asterisk" out loud is not what anyone wants. So the text is filtered on its
// way to the synthesiser only, and what the game draws on screen is untouched.
//
// Single and double asterisks are treated differently on purpose:
//
//   *grabs the controller*   a stage direction  -> dropped whole
//   **almost got it**        emphasis on words she actually says -> markers
//                            dropped, words kept
//
// That split matches how these models actually use the two, and getting it
// backwards would either mute spoken dialogue or read directions aloud.
using System.Text.RegularExpressions;

namespace AI2UCustomAI
{
    internal static class SpeechText
    {
        // Deliberately non-greedy: a reply with several actions in it must lose
        // each one separately, not everything between the first and last marker.
        static readonly Regex Emphasis = new Regex(@"\*\*(.+?)\*\*", RegexOptions.Singleline);
        // The \S after the marker is what keeps "5 * 3 = 15" spoken as written: an
        // opening action marker butts straight up against its first word, while a
        // multiplication sign has a space after it.
        static readonly Regex Action = new Regex(@"\*(\S[^*]*?)\*", RegexOptions.Singleline);

        // A reply cut off mid-action leaves one unmatched marker with the rest of
        // the direction trailing after it. Without this the voice reads it out.
        static readonly Regex Dangling = new Regex(@"\*\S[^*]*$", RegexOptions.Singleline);

        static readonly Regex Spaces = new Regex(@"\s{2,}");
        static readonly Regex SpaceBeforePunct = new Regex(@"\s+([,.!?;:])");
        static readonly Regex Speakable = new Regex(@"[\p{L}\p{N}]");

        // Returns the words worth speaking, or an empty string when the reply was
        // nothing but stage directions - the caller skips synthesis entirely in
        // that case rather than paying for a clip of silence.
        public static string ForSpeech(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            if (raw.IndexOf('*') < 0) return raw;

            string s = Emphasis.Replace(raw, "$1");
            s = Action.Replace(s, " ");
            s = Dangling.Replace(s, " ");

            s = Spaces.Replace(s, " ");
            s = SpaceBeforePunct.Replace(s, "$1");
            s = s.Trim();

            return Speakable.IsMatch(s) ? s : string.Empty;
        }
    }
}
