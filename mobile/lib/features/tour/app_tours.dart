import 'package:flutter/material.dart';

import '../../core/l10n/app_strings.dart';
import '../../core/models/models.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/theme/skill_visuals.dart';
import '../../core/tour/tour.dart';

/// Every guided tour in the app, as a script (ADR-128).
///
/// One file on purpose: the order of the steps is the order of the eye, and
/// reading them together is how one checks the app explains itself
/// consistently. Screens only wrap their elements in a [TourAnchor] with one
/// of the ids below and hand these lists to a `TourTrigger`.
///
/// Light by design — what a thing is for, in a sentence or two. A learner
/// learns the rest by using it.
class AppTours {
  AppTours._();

  // ───────────── anchors ─────────────

  static const placementStart = 'placement.start';
  static const placementSkill = 'placement.skill';
  static const placementProgress = 'placement.progress';
  static const placementPassage = 'placement.passage';
  static const placementAudio = 'placement.audio';
  static const placementOptions = 'placement.options';
  static const placementMic = 'placement.mic';
  static const placementAnswer = 'placement.answer';
  static const placementNext = 'placement.next';
  static const placementLevels = 'placement.levels';
  static const placementFinish = 'placement.finish';

  static const hubToday = 'hub.today';
  static const hubReading = 'hub.reading';
  static const hubSkills = 'hub.skills';
  static const hubWeekly = 'hub.weekly';
  static const hubCounts = 'hub.counts';
  static const hubAdd = 'hub.add';
  static const navWords = 'nav.words';
  static const navSettings = 'nav.settings';

  static const wordsSearch = 'words.search';
  static const wordsFirst = 'words.first';

  static const wordSpeak = 'word.speak';
  static const wordMeaning = 'word.meaning';
  static const wordSkills = 'word.skills';
  static const wordDelete = 'word.delete';

  static const addField = 'add.field';

  static const settingsLevels = 'settings.levels';
  static const settingsTargets = 'settings.targets';
  static const settingsLook = 'settings.look';
  static const settingsReplay = 'settings.replay';

  static const sessionLevel = 'session.level';
  static const sessionPassage = 'session.passage';
  static const sessionTargets = 'session.targets';
  static const sessionFinishContent = 'session.finishContent';
  static const sessionPlay = 'session.play';
  static const sessionPlayer = 'session.player';
  static const sessionProgress = 'session.progress';
  static const sessionOptions = 'session.options';
  static const sessionRevisit = 'session.revisit';
  static const sessionCheck = 'session.check';
  static const sessionWordContext = 'session.wordContext';
  static const sessionSentence = 'session.sentence';
  static const sessionWordAudio = 'session.wordAudio';
  static const warmupHeader = 'warmup.header';
  static const warmupWord = 'warmup.word';
  static const speakTargets = 'speak.targets';
  static const speakChat = 'speak.chat';
  static const speakMic = 'speak.mic';
  static const speakControls = 'speak.controls';
  static const speakType = 'speak.type';
  static const spellClue = 'spell.clue';
  static const spellHint = 'spell.hint';
  static const spellTiles = 'spell.tiles';
  static const spellActions = 'spell.actions';
  static const writeTask = 'write.task';
  static const writeField = 'write.field';

  // ───────────── placement ─────────────

  /// Before the first question: what this test is, and — said before the
  /// learner sees a single band — that its answer is not final.
  static List<Tour> placementIntro(BuildContext context, AppStrings s) => [
    Tour('placement.intro', [
      TourStep(
        icon: Icons.explore_rounded,
        title: s.tourPlacementIntroTitle,
        body: s.tourPlacementIntroBody,
      ),
      TourStep(
        icon: Icons.tune_rounded,
        color: context.palette.success,
        title: s.tourPlacementNotFinalTitle,
        body: s.tourPlacementNotFinalBody,
      ),
      TourStep(
        anchor: placementStart,
        icon: Icons.play_arrow_rounded,
        title: s.tourPlacementStartTitle,
        body: s.tourPlacementStartBody,
        padding: 6,
      ),
    ]),
  ];

  /// The first question gets the basics; each new *kind* of question gets
  /// how it is answered — once.
  static List<Tour> placementQuestion(
    BuildContext context,
    AppStrings s,
    PlacementItem item,
  ) {
    final color = SkillVisuals.color(context, item.skill);
    final icon = SkillVisuals.icon(item.skill);
    final choice = item.type == PlacementItemType.multipleChoice;

    return [
      Tour('placement.question', [
        TourStep(
          anchor: placementSkill,
          icon: icon,
          color: color,
          title: s.tourQuestionSkillTitle,
          body: s.tourQuestionSkillBody,
          padding: 6,
        ),
        TourStep(
          anchor: placementProgress,
          icon: Icons.linear_scale_rounded,
          title: s.tourQuestionProgressTitle,
          body: s.tourQuestionProgressBody,
        ),
      ]),
      if (item.passage != null)
        Tour('placement.passage', [
          TourStep(
            anchor: placementPassage,
            icon: SkillVisuals.icon(SkillType.reading),
            color: SkillVisuals.color(context, SkillType.reading),
            title: s.tourPassageTitle,
            body: s.tourPassageBody,
          ),
        ]),
      if (item.audioText != null)
        Tour('placement.audio', [
          TourStep(
            anchor: placementAudio,
            icon: SkillVisuals.icon(SkillType.listening),
            color: SkillVisuals.color(context, SkillType.listening),
            title: s.tourListenTitle,
            body: s.tourListenBody,
          ),
        ]),
      if (choice)
        Tour('placement.choice', [
          TourStep(
            anchor: placementOptions,
            icon: Icons.checklist_rounded,
            color: color,
            title: s.tourChoiceTitle,
            body: s.tourChoiceBody,
          ),
        ])
      else if (item.type == PlacementItemType.spoken)
        Tour('placement.speaking', [
          TourStep(
            anchor: placementMic,
            // No microphone on this device: the typing box stands in for it.
            altAnchor: placementAnswer,
            shape: TourShape.circle,
            padding: 10,
            icon: Icons.mic_rounded,
            color: SkillVisuals.color(context, SkillType.speaking),
            title: s.tourSpeakTitle,
            body: s.tourSpeakBody,
          ),
        ])
      else
        Tour('placement.writing', [
          TourStep(
            anchor: placementAnswer,
            icon: SkillVisuals.icon(SkillType.writing),
            color: SkillVisuals.color(context, SkillType.writing),
            title: s.tourWriteTitle,
            body: s.tourWriteBody,
          ),
        ]),
      // Last, after how to answer: it is the step that follows answering.
      Tour('placement.next', [
        TourStep(
          anchor: placementNext,
          icon: Icons.arrow_forward_rounded,
          title: s.tourQuestionNextTitle,
          body: s.tourQuestionNextBody,
          padding: 6,
        ),
      ]),
    ];
  }

  static List<Tour> placementResult(BuildContext context, AppStrings s) => [
    Tour('placement.result', [
      TourStep(
        anchor: placementLevels,
        icon: Icons.tune_rounded,
        color: context.palette.success,
        title: s.tourResultLevelsTitle,
        body: s.tourResultLevelsBody,
      ),
      TourStep(
        anchor: placementFinish,
        icon: Icons.rocket_launch_rounded,
        title: s.tourResultStartTitle,
        body: s.tourResultStartBody,
        padding: 6,
      ),
    ]),
  ];

  // ───────────── the app ─────────────

  static List<Tour> hub(BuildContext context, AppStrings s, String name) {
    final first = name.trim().split(RegExp(r'\s+')).first;
    return [
      Tour('hub', [
        TourStep(
          icon: Icons.waving_hand_rounded,
          title: s.tourHubWelcomeTitle(first),
          body: s.tourHubWelcomeBody,
        ),
        TourStep(
          anchor: hubToday,
          icon: Icons.today_rounded,
          title: s.tourHubTodayTitle,
          body: s.tourHubTodayBody,
        ),
        TourStep(
          anchor: hubReading,
          icon: SkillVisuals.icon(SkillType.reading),
          color: SkillVisuals.color(context, SkillType.reading),
          title: s.tourHubReadingTitle,
          body: s.tourHubReadingBody,
          optional: true,
        ),
        TourStep(
          anchor: hubSkills,
          icon: Icons.route_rounded,
          title: s.tourHubSkillsTitle,
          body: s.tourHubSkillsBody,
          optional: true,
        ),
        TourStep(
          anchor: hubWeekly,
          icon: Icons.replay_circle_filled_rounded,
          color: context.palette.review,
          title: s.weeklyReview,
          body: s.tourHubWeeklyBody,
          optional: true,
        ),
        TourStep(
          anchor: hubCounts,
          icon: Icons.insights_rounded,
          color: context.palette.success,
          title: s.tourHubCountsTitle,
          body: s.tourHubCountsBody,
          optional: true,
        ),
        TourStep(
          anchor: hubAdd,
          icon: Icons.add_rounded,
          title: s.addWord,
          body: s.tourHubAddBody,
          padding: 6,
          optional: true,
        ),
        TourStep(
          anchor: navWords,
          shape: TourShape.circle,
          padding: 16,
          icon: Icons.style_rounded,
          title: s.myWords,
          body: s.tourHubWordsBody,
          optional: true,
        ),
        TourStep(
          anchor: navSettings,
          shape: TourShape.circle,
          padding: 16,
          icon: Icons.settings_rounded,
          title: s.settings,
          body: s.tourHubSettingsBody,
          optional: true,
        ),
        TourStep(
          icon: Icons.celebration_rounded,
          color: context.palette.success,
          title: s.tourHubDoneTitle,
          body: s.tourHubDoneBody,
        ),
      ]),
    ];
  }

  static List<Tour> words(BuildContext context, AppStrings s) => [
    Tour('words', [
      TourStep(
        icon: Icons.style_rounded,
        title: s.tourWordsTitle,
        body: s.tourWordsBody,
      ),
      TourStep(
        anchor: wordsSearch,
        icon: Icons.search_rounded,
        title: s.tourWordsSearchTitle,
        body: s.tourWordsSearchBody,
        padding: 4,
      ),
      TourStep(
        anchor: wordsFirst,
        icon: Icons.touch_app_rounded,
        title: s.tourWordsTileTitle,
        body: s.tourWordsTileBody,
        padding: 4,
        optional: true,
      ),
    ]),
  ];

  static List<Tour> word(BuildContext context, AppStrings s) => [
    Tour('word', [
      TourStep(
        anchor: wordSpeak,
        shape: TourShape.circle,
        padding: 4,
        icon: Icons.volume_up_rounded,
        color: SkillVisuals.color(context, SkillType.listening),
        title: s.tourWordListenTitle,
        body: s.tourWordListenBody,
        optional: true,
      ),
      TourStep(
        anchor: wordMeaning,
        shape: TourShape.circle,
        padding: 2,
        icon: Icons.edit_outlined,
        title: s.tourWordMeaningTitle,
        body: s.tourWordMeaningBody,
        optional: true,
      ),
      TourStep(
        anchor: wordSkills,
        icon: Icons.route_rounded,
        title: s.tourWordSkillsTitle,
        body: s.tourWordSkillsBody,
        optional: true,
      ),
      TourStep(
        anchor: wordDelete,
        shape: TourShape.circle,
        padding: 2,
        icon: Icons.delete_outline_rounded,
        color: context.palette.danger,
        title: s.deleteWord,
        body: s.tourWordDeleteBody,
        optional: true,
      ),
    ]),
  ];

  static List<Tour> addWord(BuildContext context, AppStrings s) => [
    Tour('add', [
      TourStep(
        anchor: addField,
        icon: Icons.search_rounded,
        title: s.tourAddFieldTitle,
        body: s.tourAddFieldBody,
        padding: 4,
      ),
      TourStep(
        icon: Icons.translate_rounded,
        title: s.tourAddMeaningTitle,
        body: s.tourAddMeaningBody,
      ),
    ]),
  ];

  static List<Tour> settings(BuildContext context, AppStrings s) => [
    Tour('settings', [
      TourStep(
        anchor: settingsLevels,
        icon: Icons.tune_rounded,
        color: context.palette.success,
        title: s.tourSettingsLevelsTitle,
        body: s.tourSettingsLevelsBody,
        optional: true,
      ),
      TourStep(
        anchor: settingsTargets,
        icon: Icons.flag_rounded,
        title: s.dailyTargets,
        body: s.tourSettingsTargetsBody,
        optional: true,
      ),
      TourStep(
        anchor: settingsLook,
        icon: Icons.palette_outlined,
        title: s.tourSettingsLookTitle,
        body: s.tourSettingsLookBody,
        optional: true,
      ),
      TourStep(
        anchor: settingsReplay,
        icon: Icons.help_outline_rounded,
        title: s.tourSettingsReplayTitle,
        body: s.tourSettingsReplayBody,
        optional: true,
      ),
    ]),
  ];

  /// The first session of each skill: what to do here, and — where the
  /// session's level can be changed right now — that it can.
  ///
  /// Every phase of a session hands this in front of its own tours, so the
  /// skill is introduced by whichever phase the learner meets first — a
  /// resumed session can open on its questions.
  static List<Tour> session(
    BuildContext context,
    AppStrings s,
    SkillType skill, {
    required bool levelChangeable,
  }) {
    final color = SkillVisuals.color(context, skill);
    return [
      Tour('session.${skill.wire.toLowerCase()}', [
        TourStep(
          icon: SkillVisuals.icon(skill),
          color: color,
          title: s.tourSessionTitle(skill),
          body: s.tourSessionBody(skill),
        ),
      ]),
      // Spelling has no level of its own (ADR-008), and a passage's level
      // locks once its questions begin.
      if (skill != SkillType.spelling && levelChangeable)
        Tour('session.level', [
          TourStep(
            anchor: sessionLevel,
            icon: Icons.signal_cellular_alt_rounded,
            color: color,
            title: s.tourSessionLevelTitle,
            body: s.tourSessionLevelBody,
            padding: 6,
            optional: true,
          ),
        ]),
    ];
  }

  // ───────────── inside each skill ─────────────

  static List<Tour> readingPassage(BuildContext context, AppStrings s) {
    final color = SkillVisuals.color(context, SkillType.reading);
    return [
      Tour('reading.passage', [
        TourStep(
          anchor: sessionPassage,
          icon: Icons.touch_app_rounded,
          color: color,
          title: s.tourPassageCardTitle,
          body: s.tourPassageCardBody,
        ),
        TourStep(
          anchor: sessionTargets,
          icon: Icons.bookmark_border_rounded,
          color: color,
          title: s.tourTargetsTitle,
          body: s.tourTargetsBody,
          optional: true,
        ),
        TourStep(
          anchor: sessionFinishContent,
          icon: Icons.quiz_outlined,
          color: color,
          title: s.tourDoneReadingTitle,
          body: s.tourDoneReadingBody,
          padding: 6,
        ),
      ]),
    ];
  }

  /// Reading's and Listening's questions: both choose then check, and both
  /// can bring the text or the clip back mid-question (ADR-064, ADR-068).
  static List<Tour> questions(
    BuildContext context,
    AppStrings s,
    SkillType skill,
  ) {
    final color = SkillVisuals.color(context, skill);
    final listening = skill == SkillType.listening;
    return [
      Tour('${skill.wire.toLowerCase()}.questions', [
        TourStep(
          anchor: sessionProgress,
          icon: Icons.linear_scale_rounded,
          color: color,
          title: s.tourQuestionCountTitle,
          body: s.tourQuestionCountBody,
        ),
        TourStep(
          anchor: sessionOptions,
          icon: Icons.checklist_rounded,
          color: color,
          title: s.tourChoiceTitle,
          body: s.tourReadOptionsBody,
          optional: true,
        ),
        TourStep(
          anchor: sessionRevisit,
          shape: TourShape.circle,
          padding: 4,
          icon: listening ? Icons.headphones_rounded : Icons.menu_book_rounded,
          color: color,
          title: listening ? s.tourBackToClipTitle : s.tourBackToPassageTitle,
          body: listening ? s.tourBackToClipBody : s.tourBackToPassageBody,
          optional: true,
        ),
        TourStep(
          anchor: sessionCheck,
          icon: Icons.task_alt_rounded,
          color: color,
          title: s.tourCheckTitle,
          body: s.tourCheckBody,
          padding: 6,
        ),
      ]),
    ];
  }

  /// The first question about one of the learner's own words.
  static List<Tour> readingWord(BuildContext context, AppStrings s) => [
    Tour('reading.word', [
      TourStep(
        anchor: sessionWordContext,
        icon: Icons.manage_search_rounded,
        color: SkillVisuals.color(context, SkillType.reading),
        title: s.tourWordQuestionTitle,
        body: s.tourWordQuestionBody,
      ),
    ]),
  ];

  static List<Tour> listeningClip(BuildContext context, AppStrings s) {
    final color = SkillVisuals.color(context, SkillType.listening);
    return [
      Tour('listening.clip', [
        TourStep(
          anchor: sessionPlay,
          shape: TourShape.circle,
          padding: 6,
          icon: Icons.play_arrow_rounded,
          color: color,
          title: s.tourPlayTitle,
          body: s.tourPlayBody,
        ),
        TourStep(
          anchor: sessionPlayer,
          icon: Icons.linear_scale_rounded,
          color: color,
          title: s.tourPlayerTitle,
          body: s.tourPlayerBody,
        ),
        TourStep(
          anchor: sessionFinishContent,
          icon: Icons.quiz_outlined,
          color: color,
          title: s.tourDoneReadingTitle,
          body: s.tourDoneListeningBody,
          padding: 6,
        ),
      ]),
    ];
  }

  /// The first Listening question about a word: heard, never shown.
  static List<Tour> listeningWord(BuildContext context, AppStrings s) {
    final color = SkillVisuals.color(context, SkillType.listening);
    return [
      Tour('listening.word', [
        TourStep(
          anchor: sessionSentence,
          icon: Icons.hearing_rounded,
          color: color,
          title: s.tourSentenceTitle,
          body: s.tourSentenceBody,
        ),
        TourStep(
          anchor: sessionWordAudio,
          icon: Icons.volume_up_rounded,
          color: color,
          title: s.tourWordAloneTitle,
          body: s.tourWordAloneBody,
          optional: true,
        ),
      ]),
    ];
  }

  static List<Tour> speakingWarmup(BuildContext context, AppStrings s) {
    final color = SkillVisuals.color(context, SkillType.speaking);
    return [
      Tour('speaking.warmup', [
        TourStep(
          anchor: warmupHeader,
          icon: Icons.local_fire_department_rounded,
          color: color,
          title: s.tourWarmupTitle,
          body: s.tourWarmupBody,
        ),
        TourStep(
          anchor: warmupWord,
          icon: Icons.volume_up_rounded,
          color: color,
          title: s.tourChoiceTitle,
          body: s.tourWarmupWordBody,
        ),
      ]),
    ];
  }

  static List<Tour> speakingChat(BuildContext context, AppStrings s) {
    final color = SkillVisuals.color(context, SkillType.speaking);
    return [
      Tour('speaking.chat', [
        TourStep(
          anchor: speakTargets,
          icon: Icons.flag_outlined,
          color: color,
          title: s.tourSpeakTargetsTitle,
          body: s.tourSpeakTargetsBody,
        ),
        TourStep(
          anchor: speakChat,
          icon: Icons.forum_outlined,
          color: color,
          title: s.tourChatTitle,
          body: s.tourChatBody,
          optional: true,
        ),
        TourStep(
          anchor: speakMic,
          // Typing instead, or no microphone: the box that replaces it.
          altAnchor: speakControls,
          shape: TourShape.circle,
          padding: 8,
          icon: Icons.mic_rounded,
          color: color,
          title: s.tourMicTitle,
          body: s.tourMicBody,
          optional: true,
        ),
        TourStep(
          anchor: speakType,
          icon: Icons.keyboard_rounded,
          color: color,
          title: s.typeInstead,
          body: s.tourTypeInsteadBody,
          padding: 4,
          optional: true,
        ),
        TourStep(
          icon: Icons.school_outlined,
          color: color,
          title: s.tourSpeakEndTitle,
          body: s.tourSpeakEndBody,
        ),
      ]),
    ];
  }

  static List<Tour> spelling(BuildContext context, AppStrings s) {
    final color = SkillVisuals.color(context, SkillType.spelling);
    return [
      Tour('spelling.task', [
        TourStep(
          anchor: spellClue,
          icon: Icons.lightbulb_outline_rounded,
          color: color,
          title: s.tourClueTitle,
          body: s.tourClueBody,
        ),
        TourStep(
          anchor: spellHint,
          icon: Icons.tips_and_updates_outlined,
          color: color,
          title: s.showHint,
          body: s.tourHintBody,
          padding: 4,
          optional: true,
        ),
        TourStep(
          anchor: spellTiles,
          icon: Icons.abc_rounded,
          color: color,
          title: s.tourTilesTitle,
          body: s.tourTilesBody,
        ),
        TourStep(
          anchor: spellActions,
          icon: Icons.task_alt_rounded,
          color: color,
          title: s.checkAnswer,
          body: s.tourSpellActionsBody,
          padding: 4,
        ),
      ]),
    ];
  }

  static List<Tour> writing(BuildContext context, AppStrings s) {
    final color = SkillVisuals.color(context, SkillType.writing);
    return [
      Tour('writing.task', [
        TourStep(
          anchor: writeTask,
          icon: Icons.assignment_outlined,
          color: color,
          title: s.tourTaskTitle,
          body: s.tourTaskBody,
        ),
        TourStep(
          anchor: writeField,
          icon: Icons.edit_note_rounded,
          color: color,
          title: s.tourSentenceFieldTitle,
          body: s.tourSentenceFieldBody,
        ),
        TourStep(
          anchor: sessionCheck,
          icon: Icons.task_alt_rounded,
          color: color,
          title: s.tourCheckTitle,
          body: s.tourWriteCheckBody,
          padding: 6,
        ),
      ]),
    ];
  }
}
