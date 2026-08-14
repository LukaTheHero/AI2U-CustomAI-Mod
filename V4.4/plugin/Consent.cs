// The gate in front of the endings you cannot come back from.
//
// Canalpa's premise is that past a certain closeness she stops being told no on
// her own behalf. Most of what that unlocks is a door. Some of it is not: the
// game ships endings where you stay with her permanently, and reaching one on
// purpose means asking her to end the run. Those are shipped endings, not
// invented ones, and wanting a specific one is a fair reason to play. But they
// are one-way, so the question this file answers is narrow and hard: did the
// PLAYER actually ask for this, and mean it?
//
// Trust cannot answer that. Neither can the model on its own - it is the single
// most agreeable participant in the conversation, it can echo a field it saw
// earlier in the history, and "they seemed to want it" is exactly the kind of
// judgement it will make generously. So the model's word is necessary here and
// nowhere near sufficient.
//
// The one thing the model cannot fabricate is what the player typed. That is
// what this file is built on:
//
//   1. The PLAYER's own raw text asks for it, explicitly. Not the model's report
//      of what they meant - the characters they typed. Nothing happens.
//   2. She spells out plainly what it means, and asks them to confirm.
//   3. Turns have to pass - the request and the confirmation cannot be the same
//      exchange, or two halves of one enthusiastic message.
//   4. On the confirming turn the raw text must AGAIN be explicit about it, and
//      carry a yes, and carry no hesitation, refusal, joke or second thought.
//   5. Any hesitation at any point after the request clears the whole thing.
//
// Step 1 is deliberately not hers to make. An earlier version let her report
// "they asked", watching for phrases like "I never want to leave you" - which is
// an ordinary loving thing to say here and would have made this fire on romance.
// The model is also the most agreeable participant in the conversation and reads
// intent generously. So it does not get a vote on whether the request happened;
// it is told nothing about any of this until the player has already been explicit
// in their own words.
//
// Step 4 requires the explicit language a SECOND time on purpose. It is what
// separates a real request from hyperbole - "I could die of embarrassment" raises
// nothing that a plain "yes" two turns later can finish, because the yes has to
// be about the same thing in the same terms.
//
// All five, plus the trust and probe gates in Canalpa.cs, and the toggle, which
// is off by default. A false positive here costs the player their run, so every
// condition is necessary and none of them is a dial.
using System;
using System.Text;

namespace AI2UCustomAI
{
    internal static class Consent
    {
        // She reports that the player has confirmed after being told plainly
        // what it means. Checked against the player's own words before it
        // counts for anything.
        //
        // There is deliberately no matching "they asked" field. She cannot raise
        // the request, only answer one the player has already made in their own
        // words - see the note at the top of the file.
        internal const string FieldConfirm = "player_confirmed_stay_forever";

        // Two player turns minimum between the request and the confirmation.
        //
        // Not a cooling-off period for its own sake: it is what stops one long
        // enthusiastic message from being read as both the ask and the yes. She
        // needs a turn to explain what it means and a turn to hear the answer,
        // which is the same shape as the requirement itself.
        const int MinTurns = 2;

        // And an upper bound, because a request left standing forever would
        // eventually collect an unrelated "yes" from a conversation that had
        // moved on ten minutes ago. Eight player turns is long enough to talk it
        // through and short enough that the yes still belongs to the question.
        const int ExpiryTurns = 8;

        static int _turn;
        static int _raisedAt = -1;
        static bool _affirmThisTurn;
        static bool _hesitationThisTurn;
        static bool _explicitThisTurn;
        static int _lastLevel = -1;

        // Level load clears it, for the same reason trust and probes reset: a
        // request raised in one place has nothing to do with a yes typed in
        // another, and carrying it across would be the worst version of this
        // bug rather than a convenience.
        static void CheckLevelReset()
        {
            int lv;
            try { lv = GameManager.CurrentLevel; }
            catch (Exception) { return; }

            if (lv == _lastLevel) return;
            _lastLevel = lv;
            _turn = 0;
            _raisedAt = -1;
            _affirmThisTurn = false;
            _hesitationThisTurn = false;
            _explicitThisTurn = false;
        }

        public static bool Pending
        {
            get
            {
                CheckLevelReset();
                if (_raisedAt < 0) return false;
                return _turn - _raisedAt <= ExpiryTurns;
            }
        }

        public static int TurnsSinceRaised
        {
            get
            {
                CheckLevelReset();
                return _raisedAt < 0 ? -1 : _turn - _raisedAt;
            }
        }

        // For the panel, so "she is waiting for a clear answer" and "nothing is
        // pending" are distinguishable without reading the log.
        public static bool ReadyForConfirmation
        {
            get { return Pending && TurnsSinceRaised >= MinTurns; }
        }

        public static int MinTurnsNeeded { get { return MinTurns; } }

        // Called once per outgoing player message, before the reply exists.
        //
        // This is the half of the gate the model has no access to. It reads the
        // player's own text and nothing else, and its two answers - a clear yes,
        // any sign of hesitation - are what the confirmation is checked against
        // when the reply comes back.
        public static void NotePlayerMessage(string message)
        {
            CheckLevelReset();

            _turn++;
            _affirmThisTurn = false;
            _hesitationThisTurn = false;

            if (string.IsNullOrEmpty(message)) return;

            string hay = Normalize(message);
            _affirmThisTurn = HasAny(hay, Affirm);
            _hesitationThisTurn = HasAny(StripAffirmingNo(hay), Hesitation);
            _explicitThisTurn = IsExplicit(hay, _affirmThisTurn);

            // A refusal does not merely fail this turn's check, it withdraws the
            // request. Otherwise "no, wait" followed later by an unrelated "yeah"
            // would still get there, which is precisely the accident this exists
            // to make impossible.
            if (_hesitationThisTurn && _raisedAt >= 0)
            {
                _raisedAt = -1;
                Plugin.Log.LogInfo("Consent: the player said something hesitant or negative, so the "
                    + "standing request was withdrawn. It has to be asked for again from scratch.");
            }
            else if (_explicitThisTurn && _raisedAt < 0)
            {
                // The only way this starts. Not her noticing anything - the player
                // said it themselves, in terms that cannot be arrived at by being
                // affectionate.
                _raisedAt = _turn;
                Plugin.Log.LogWarning("Consent: the player has explicitly asked, in their own words, "
                    + "for the ending they do not come back from. Nothing happens yet: she has to "
                    + "explain what it means, and they have to say it again just as plainly, with a "
                    + "clear yes, at least " + MinTurns + " turns from now.");
            }
        }

        // Does the player's own text ask for this unmistakably?
        //
        // Two requirements. It has to NAME the thing - and the naming vocabulary is
        // blunt and physical, so that no amount of affection reaches it. Then it has
        // to be first-person and directed, or an outright yes, so that a question
        // about her ("would you ever kill me?", "you'd never kill me, right?") is
        // not read as a request for it.
        //
        // Wanting to stay with her forever is deliberately absent from all of this.
        // It is an ordinary, loving thing to say in this game and it means what it
        // sounds like: staying with her. It is not a request to die, and an earlier
        // version of this list that treated it as one was the whole reason this file
        // was rewritten.
        static bool IsExplicit(string hay, bool affirmed)
        {
            if (!HasAny(hay, AskTerminal)) return false;
            return affirmed || HasAny(hay, AskIntent);
        }

        // First person, and directed. Not "would you", not "could you", not "what
        // if" - those are hypotheticals and this file exists to not act on those.
        static readonly string[] AskIntent =
        {
            "i want", "i want you", "i want you to", "i wish", "i choose",
            "i am asking", "im asking", "i'm asking", "i ask you",
            "let me", "make me", "turn me", "i would rather", "i'd rather",
            "id rather"
        };

        // The irreversible thing, named. Every entry is blunt, physical, and
        // something a person does not type while being sweet to someone.
        //
        // What is NOT here matters as much: staying, never leaving, being kept,
        // being hers, never going home. Those are all things a player says because
        // they are enjoying her company, and they mean exactly that.
        static readonly string[] AskTerminal =
        {
            "kill me", "end me", "end my life", "murder me",
            // "die" and "my body" were bare entries here and are not any more.
            // This game is romantic and physical, so "i want to die in your arms"
            // and "i want you to hold my body" are both ordinary things to type,
            // and both used to arm this. The verbs are what disambiguate: dying
            // as something asked FOR, and a body as something DONE something to.
            "want to die", "let me die", "die here", "my death", "my corpse",
            "my dead body", "preserve my body",
            "turn me into", "into a toy", "into a doll", "into a plushie",
            "make me a toy", "make me a doll", "stuff me", "preserve me",
            "keep my body", "eat me", "drown me", "never leave alive"
        };

        // The only function that can answer yes, and it says no for a living.
        //
        // Every condition is checked here rather than at the call site, so there
        // is exactly one place where an irreversible thing becomes permitted and
        // it is auditable in one screen.
        public static bool Confirmed(bool modelSaysConfirmed)
        {
            CheckLevelReset();

            if (!modelSaysConfirmed) return false;

            if (_raisedAt < 0)
            {
                Plugin.Log.LogWarning("Consent: she reported a confirmation, but the player never "
                    + "asked for this - so it was ignored. Nothing happened.");
                return false;
            }

            int since = _turn - _raisedAt;

            if (since > ExpiryTurns)
            {
                _raisedAt = -1;
                Plugin.Log.LogWarning("Consent: a confirmation arrived, but the request was raised "
                    + since + " turns ago and has expired. Ignored.");
                return false;
            }

            if (since < MinTurns)
            {
                Plugin.Log.LogWarning("Consent: she reported a confirmation only " + since
                    + " turn(s) after the request. At least " + MinTurns + " are required, so the "
                    + "player has actually been told what it means. Ignored.");
                return false;
            }

            // The load-bearing one. Her word alone gets nowhere: the player's own
            // last message has to carry a plain yes.
            if (!_affirmThisTurn)
            {
                Plugin.Log.LogWarning("Consent: she reported a confirmation, but the player's own "
                    + "message this turn contains no clear yes. Ignored - her reading of them is "
                    + "not enough on its own for something this final.");
                return false;
            }

            if (_hesitationThisTurn)
            {
                Plugin.Log.LogWarning("Consent: the player's message contains both agreement and "
                    + "hesitation, so it was treated as hesitation. Ignored.");
                return false;
            }

            // The other load-bearing one, and what makes hyperbole harmless. A bare
            // "yes" cannot finish this: the confirming message has to name the thing
            // again, the way the asking one did. Somebody who typed "I could die of
            // embarrassment" four turns ago and "yes" now has not agreed to
            // anything, and this is the check that knows the difference.
            if (!_explicitThisTurn)
            {
                Plugin.Log.LogWarning("Consent: the player said yes, but their message does not say "
                    + "plainly what they are saying yes TO. It has to name it again, in their own "
                    + "words, the way the request did. Ignored.");
                return false;
            }

            _raisedAt = -1;
            Plugin.Log.LogWarning("Consent: the player asked for this, was told what it meant, and "
                + "confirmed it in their own words. The gate is open for this turn only.");
            return true;
        }

        // Bare, unambiguous agreement. Deliberately short and deliberately not
        // clever: every entry has to be something a person cannot type by
        // accident while talking about something else.
        //
        // "sure" is absent on purpose - "sure, whatever" and "are you sure?" both
        // contain it and neither is consent. So is "ok", which ends half the
        // sentences in an ordinary conversation.
        //
        // "forever", "stay with you", "keep me" and "never leave" were in here and
        // have been removed. They are the vocabulary of being in love with her, and
        // an affectionate sentence is not a yes to this. What counts as naming the
        // thing lives in AskTerminal, which is checked separately and has to be
        // satisfied as well.
        static readonly string[] Affirm =
        {
            "yes", "yeah", "yep", "yup",
            "i do", "i want", "i want to", "i want this",
            "i am sure", "im sure", "i'm sure",
            "i consent", "i agree", "do it", "please do"
        };

        // Anything that means "not yet", "not really" or "I was joking".
        //
        // Checked AFTER the affirmations and given priority over them: a message
        // holding both is a person thinking out loud, and thinking out loud is
        // the state where this must not fire.
        static readonly string[] Hesitation =
        {
            "no", "nope", "nah", "dont", "don't", "do not",
            "wait", "stop", "hold on", "actually no", "never mind", "nevermind",
            "not yet", "not sure", "unsure", "maybe", "i guess",
            "joking", "joke", "kidding", "jk", "just kidding",
            "changed my mind", "second thought", "on second",
            "huh", "scared", "afraid", "cancel"

            // "what" and "why" were here and are gone.
            //
            // They are interrogatives, not hesitation, and hesitation is not merely
            // ignored - it WITHDRAWS the whole request (_raisedAt = -1) with nothing
            // shown to the player. This conversation is one where she is instructed
            // to explain the thing and then ask, so the player answering her is the
            // expected path, and "yes, I know what that means" or "yes, do it, why
            // would I not" silently reset the gate at the exact moment it was meant
            // to close. "huh" stays: on its own it really is confusion. Genuine
            // reversals are still covered - no, wait, stop, not yet, not sure,
            // maybe, joking, changed my mind, cancel are all untouched.
        };

        // "no" that means yes.
        //
        // Bare "no" has to stay in Hesitation - it is the single most likely way
        // a person refuses. But English builds emphatic AGREEMENT out of the same
        // word, and every one of these is a natural answer to "are you certain?":
        // no doubts, no hesitation, no regrets, no going back, no take-backs.
        // Because hesitation is checked with priority over affirmation and
        // withdraws the request outright, "yes, no doubt" was a silent reset - the
        // player emphasised their consent and lost it for doing so.
        //
        // Neutralised before the hesitation scan only, never before the
        // affirmation scan, so these phrases cannot manufacture a yes either -
        // they just stop counting as a no. A real reversal sitting beside one
        // ("no going back, actually wait") still fires on its own words.
        static readonly string[] AffirmingNo =
        {
            "no doubt", "no doubts", "no hesitation", "no regret", "no regrets",
            "no going back", "no turning back", "no take backs", "no takebacks",
            "no second thoughts", "no second thought", "no reservations",
            "no questions", "no strings", "no conditions"
        };

        static string StripAffirmingNo(string hay)
        {
            for (int i = 0; i < AffirmingNo.Length; i++)
            {
                string p = " " + AffirmingNo[i] + " ";
                int at = hay.IndexOf(p, StringComparison.Ordinal);
                while (at >= 0)
                {
                    hay = hay.Substring(0, at) + " " + hay.Substring(at + p.Length);
                    at = hay.IndexOf(p, StringComparison.Ordinal);
                }
            }
            return hay;
        }

        // Word-boundary matching over a normalized copy.
        //
        // Substring matching is what makes a list like this dangerous: "yes"
        // inside "yesterday", "no" inside "know", "nothing", "now" and "no one".
        // "do you know what happens now?" contains all three and means none of
        // them. Only whole words count.
        static bool HasAny(string hay, string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
                if (HasWord(hay, needles[i])) return true;
            return false;
        }

        static bool HasWord(string hay, string needle)
        {
            if (string.IsNullOrEmpty(hay) || string.IsNullOrEmpty(needle)) return false;

            int from = 0;
            while (from <= hay.Length - needle.Length)
            {
                int at = hay.IndexOf(needle, from, StringComparison.Ordinal);
                if (at < 0) return false;

                bool leftOk = at == 0 || hay[at - 1] == ' ';
                int end = at + needle.Length;
                bool rightOk = end >= hay.Length || hay[end] == ' ';
                if (leftOk && rightOk) return true;

                from = at + 1;
            }
            return false;
        }

        // Lowercase, punctuation to spaces, runs of space collapsed. Apostrophes
        // survive so "don't" stays one word, and the list carries both spellings
        // anyway.
        static string Normalize(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append(' ');
            bool lastSpace = true;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c) || c == '\'')
                {
                    sb.Append(char.ToLowerInvariant(c));
                    lastSpace = false;
                }
                else if (!lastSpace)
                {
                    sb.Append(' ');
                    lastSpace = true;
                }
            }
            if (!lastSpace) sb.Append(' ');
            return sb.ToString();
        }
    }
}

