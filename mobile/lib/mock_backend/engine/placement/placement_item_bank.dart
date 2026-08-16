import '../../../core/models/enums.dart';

/// ⚠️ DISPOSABLE DEVELOPMENT COMPONENT — the C# backend owns this in Phase 5.
///
/// The calibrated-by-hand item pool the adaptive placement test draws from.
///
/// Each item carries the CEFR band it was written for; [AbilityScale] turns that
/// band into a Rasch difficulty. When real response data exists, a measured
/// difficulty replaces the band-derived one and nothing else in the algorithm
/// changes — that is the whole point of keeping the bank separate from the
/// estimator (`docs/06-PLACEMENT-ALGORITHM.md` §9).
class PlacementItemBank {
  const PlacementItemBank._();

  /// Skills that receive a CEFR level from placement.
  ///
  /// Spelling is deliberately absent: it is measured (see [spelling]) but a
  /// CEFR band is not a meaningful description of orthographic accuracy, so the
  /// product does not assign one (demo review §4, §46).
  static const List<SkillType> cefrSkills = [
    SkillType.reading,
    SkillType.listening,
    SkillType.speaking,
    SkillType.writing,
  ];

  static List<BankItem> forSkill(SkillType skill) => switch (skill) {
        SkillType.reading => reading,
        SkillType.listening => listening,
        SkillType.speaking => speaking,
        SkillType.writing => writing,
        SkillType.spelling => spelling,
      };

  // ── Reading ───────────────────────────────────────────────────────────────
  static const List<BankItem> reading = [
    BankItem(
      id: 'rd_a1_1',
      skill: SkillType.reading,
      level: CefrLevel.a1,
      passage: 'Nora has a small cat. The cat is black and white. '
          'It sleeps on her bed every night.',
      prompt: 'Where does the cat sleep?',
      options: ['On her bed', 'In the garden', 'Under the table', 'On a chair'],
      correctAnswer: 'On her bed',
    ),
    BankItem(
      id: 'rd_a1_2',
      skill: SkillType.reading,
      level: CefrLevel.a1,
      prompt: 'Choose the word that completes: "I ___ to school every day."',
      options: ['go', 'goes', 'going', 'gone'],
      correctAnswer: 'go',
    ),
    BankItem(
      id: 'rd_a2_1',
      skill: SkillType.reading,
      level: CefrLevel.a2,
      passage: 'Sami works in a small office near the station. Every morning he '
          'takes the train at seven because the roads are busy. He likes his '
          'job, but he hopes to work from home two days a week next year.',
      prompt: 'Why does Sami take the train?',
      options: [
        'Because the roads are busy',
        'Because he does not own a car',
        'Because the train is free',
        'Because he lives far from the city',
      ],
      correctAnswer: 'Because the roads are busy',
    ),
    BankItem(
      id: 'rd_a2_2',
      skill: SkillType.reading,
      level: CefrLevel.a2,
      prompt: 'Choose the word that completes: '
          '"She was tired, ___ she finished the work."',
      options: ['but', 'because', 'so that', 'unless'],
      correctAnswer: 'but',
    ),
    BankItem(
      id: 'rd_b1_1',
      skill: SkillType.reading,
      level: CefrLevel.b1,
      passage: 'The library extended its opening hours last month. Staff '
          'expected students to arrive in the evening, but most of the new '
          'visitors came before nine in the morning. The manager now plans to '
          'open earlier instead of closing later.',
      prompt: 'What surprised the library staff?',
      options: [
        'When the new visitors arrived',
        'How few visitors came',
        'That students preferred another library',
        'That the manager changed the plan',
      ],
      correctAnswer: 'When the new visitors arrived',
    ),
    BankItem(
      id: 'rd_b1_2',
      skill: SkillType.reading,
      level: CefrLevel.b1,
      prompt: 'Choose the word that completes: '
          '"The results were ___ enough to change the plan."',
      options: ['significant', 'signature', 'signal', 'signing'],
      correctAnswer: 'significant',
    ),
    BankItem(
      id: 'rd_b2_1',
      skill: SkillType.reading,
      level: CefrLevel.b2,
      passage: 'Critics of the policy argue that it addresses the symptom '
          'rather than the cause. Its defenders counter that no long-term '
          'remedy is available yet, and that leaving the symptom untreated '
          'would be worse than an imperfect intervention.',
      prompt: 'What do the defenders of the policy claim?',
      options: [
        'An imperfect measure is better than none for now',
        'The policy removes the underlying cause',
        'Critics have misread the evidence entirely',
        'A long-term remedy is already available',
      ],
      correctAnswer: 'An imperfect measure is better than none for now',
    ),
    BankItem(
      id: 'rd_b2_2',
      skill: SkillType.reading,
      level: CefrLevel.b2,
      prompt: 'Choose the word that completes: '
          '"The evidence was ___ , so the committee postponed its decision."',
      options: ['inconclusive', 'inconsiderate', 'incomparable', 'inconvenient'],
      correctAnswer: 'inconclusive',
    ),
    BankItem(
      id: 'rd_c1_1',
      skill: SkillType.reading,
      level: CefrLevel.c1,
      passage: 'What the report presents as a finding is, on closer reading, an '
          'assumption carried over from the earlier study. The authors never '
          'test it; they simply inherit it, and the conclusions rest on that '
          'inheritance rather than on the data gathered here.',
      prompt: 'What is the writer\'s main criticism?',
      options: [
        'An untested assumption is treated as though it were evidence',
        'The data were gathered using the wrong method',
        'The earlier study has been misquoted',
        'The conclusions contradict the data presented',
      ],
      correctAnswer:
          'An untested assumption is treated as though it were evidence',
    ),
    BankItem(
      id: 'rd_c1_2',
      skill: SkillType.reading,
      level: CefrLevel.c1,
      prompt: 'Choose the phrase that completes: '
          '"Her argument, ___ , fails to account for the exceptions."',
      options: [
        'compelling though it is',
        'compelling as it be',
        'that it is compelling',
        'being compelled',
      ],
      correctAnswer: 'compelling though it is',
    ),
  ];

  // ── Listening ─────────────────────────────────────────────────────────────
  // `audioText` is spoken by TTS; the learner never sees it (demo review §34).
  static const List<BankItem> listening = [
    BankItem(
      id: 'ls_a1_1',
      skill: SkillType.listening,
      level: CefrLevel.a1,
      audioText: 'The shop opens at nine and closes at five.',
      prompt: 'When does the shop close?',
      options: ['At five', 'At nine', 'At seven', 'At three'],
      correctAnswer: 'At five',
    ),
    BankItem(
      id: 'ls_a1_2',
      skill: SkillType.listening,
      level: CefrLevel.a1,
      audioText: 'My brother is a teacher. He works at a school near our house.',
      prompt: 'What is the brother\'s job?',
      options: ['A teacher', 'A driver', 'A doctor', 'A student'],
      correctAnswer: 'A teacher',
    ),
    BankItem(
      id: 'ls_a2_1',
      skill: SkillType.listening,
      level: CefrLevel.a2,
      audioText: 'The meeting will start at half past nine in the main hall. '
          'Please bring your notebook, because the schedule will change '
          'next week.',
      prompt: 'When does the meeting start?',
      options: ['09:30', '08:30', '10:00', '09:00'],
      correctAnswer: '09:30',
    ),
    BankItem(
      id: 'ls_a2_2',
      skill: SkillType.listening,
      level: CefrLevel.a2,
      audioText: 'I wanted to walk to the museum, but it started raining, '
          'so I took the bus instead.',
      prompt: 'How did the speaker travel?',
      options: ['By bus', 'On foot', 'By car', 'By train'],
      correctAnswer: 'By bus',
    ),
    BankItem(
      id: 'ls_b1_1',
      skill: SkillType.listening,
      level: CefrLevel.b1,
      audioText: 'Researchers say the new method saves time, although it costs '
          'more than the traditional approach.',
      prompt: 'What is the disadvantage of the new method?',
      options: [
        'It costs more',
        'It takes longer',
        'It is less accurate',
        'It needs more people',
      ],
      correctAnswer: 'It costs more',
    ),
    BankItem(
      id: 'ls_b1_2',
      skill: SkillType.listening,
      level: CefrLevel.b1,
      audioText: 'We had booked the hall for Saturday, but the caterer was not '
          'available, so we moved everything to the following weekend.',
      prompt: 'Why was the event moved?',
      options: [
        'The caterer could not come',
        'The hall was double-booked',
        'Not enough guests replied',
        'The weather was bad',
      ],
      correctAnswer: 'The caterer could not come',
    ),
    BankItem(
      id: 'ls_b2_1',
      skill: SkillType.listening,
      level: CefrLevel.b2,
      audioText: 'I am not saying the proposal is unworkable. I am saying that '
          'the timeline attached to it is, and that is a different objection '
          'entirely.',
      prompt: 'What is the speaker objecting to?',
      options: [
        'The schedule, not the proposal itself',
        'The proposal as a whole',
        'The cost of the proposal',
        'The people who wrote the proposal',
      ],
      correctAnswer: 'The schedule, not the proposal itself',
    ),
    BankItem(
      id: 'ls_b2_2',
      skill: SkillType.listening,
      level: CefrLevel.b2,
      audioText: 'Attendance rose for three consecutive months, then levelled '
          'off. Rather than a decline, what we are seeing is the market '
          'reaching its natural ceiling.',
      prompt: 'What does the speaker conclude?',
      options: [
        'Growth has stopped because the market is saturated',
        'Attendance is falling sharply',
        'The three-month rise was a measurement error',
        'The market will keep growing quickly',
      ],
      correctAnswer: 'Growth has stopped because the market is saturated',
    ),
    BankItem(
      id: 'ls_c1_1',
      skill: SkillType.listening,
      level: CefrLevel.c1,
      audioText: 'To be fair to him, he did flag the risk early. What he did '
          'not do, and this is where the criticism lands, was insist on it '
          'once the decision had gone the other way.',
      prompt: 'What is the speaker\'s criticism?',
      options: [
        'He raised the risk but did not press it afterwards',
        'He failed to notice the risk at all',
        'He raised the risk far too late',
        'He overstated a risk that never materialised',
      ],
      correctAnswer: 'He raised the risk but did not press it afterwards',
    ),
  ];

  // ── Speaking ──────────────────────────────────────────────────────────────
  // Free-response prompts, evaluated against a rubric by the AI service in
  // Phase 6 (demo review §5.3). The mock scores them heuristically.
  static const List<BankItem> speaking = [
    BankItem(
      id: 'sp_a1_1',
      skill: SkillType.speaking,
      level: CefrLevel.a1,
      type: PlacementItemType.freeText,
      prompt: 'Introduce yourself in one or two sentences. '
          'Say your name and where you live.',
      expectedWords: 8,
    ),
    BankItem(
      id: 'sp_a2_1',
      skill: SkillType.speaking,
      level: CefrLevel.a2,
      type: PlacementItemType.freeText,
      prompt: 'Describe what you did yesterday. Use two or three sentences.',
      expectedWords: 16,
    ),
    BankItem(
      id: 'sp_b1_1',
      skill: SkillType.speaking,
      level: CefrLevel.b1,
      type: PlacementItemType.freeText,
      prompt: 'Someone asks: "Could you explain what you are studying and why '
          'you chose it?" Answer them.',
      expectedWords: 28,
    ),
    BankItem(
      id: 'sp_b2_1',
      skill: SkillType.speaking,
      level: CefrLevel.b2,
      type: PlacementItemType.freeText,
      prompt: 'Some people learn better alone, others in a group. Give your '
          'view and one reason for it.',
      expectedWords: 40,
    ),
    BankItem(
      id: 'sp_c1_1',
      skill: SkillType.speaking,
      level: CefrLevel.c1,
      type: PlacementItemType.freeText,
      prompt: 'A colleague proposes a plan you partly disagree with. Explain '
          'which part you accept, which you do not, and why.',
      expectedWords: 55,
    ),
  ];

  // ── Writing ───────────────────────────────────────────────────────────────
  static const List<BankItem> writing = [
    BankItem(
      id: 'wr_a1_1',
      skill: SkillType.writing,
      level: CefrLevel.a1,
      type: PlacementItemType.freeText,
      prompt: 'Write one sentence about your family.',
      expectedWords: 6,
    ),
    BankItem(
      id: 'wr_a2_1',
      skill: SkillType.writing,
      level: CefrLevel.a2,
      type: PlacementItemType.freeText,
      prompt: 'Write two sentences describing what you usually do to study '
          'English.',
      expectedWords: 16,
    ),
    BankItem(
      id: 'wr_b1_1',
      skill: SkillType.writing,
      level: CefrLevel.b1,
      type: PlacementItemType.freeText,
      prompt: 'Write a short paragraph about a skill you would like to learn '
          'and why.',
      expectedWords: 30,
    ),
    BankItem(
      id: 'wr_b2_1',
      skill: SkillType.writing,
      level: CefrLevel.b2,
      type: PlacementItemType.freeText,
      prompt: 'Write a short paragraph arguing for or against working from '
          'home. Support your view with one example.',
      expectedWords: 45,
    ),
    BankItem(
      id: 'wr_c1_1',
      skill: SkillType.writing,
      level: CefrLevel.c1,
      type: PlacementItemType.freeText,
      prompt: 'Summarise an idea you have changed your mind about, and explain '
          'what changed it.',
      expectedWords: 60,
    ),
  ];

  // ── Spelling ──────────────────────────────────────────────────────────────
  // Measured, never levelled. The outcome is an accuracy figure and the input
  // mode the learner needs, not a CEFR band (demo review §46).
  static const List<BankItem> spelling = [
    BankItem(
      id: 'sl_1',
      skill: SkillType.spelling,
      level: CefrLevel.a1,
      prompt: 'Which spelling is correct?',
      options: ['because', 'becuase', 'becouse', 'becaus'],
      correctAnswer: 'because',
    ),
    BankItem(
      id: 'sl_2',
      skill: SkillType.spelling,
      level: CefrLevel.a2,
      prompt: 'Which spelling is correct?',
      options: ['friend', 'freind', 'frend', 'friand'],
      correctAnswer: 'friend',
    ),
    BankItem(
      id: 'sl_3',
      skill: SkillType.spelling,
      level: CefrLevel.b1,
      prompt: 'Which spelling is correct?',
      options: ['environment', 'enviroment', 'envirnoment', 'enviornment'],
      correctAnswer: 'environment',
    ),
    BankItem(
      id: 'sl_4',
      skill: SkillType.spelling,
      level: CefrLevel.b1Plus,
      prompt: 'Which spelling is correct?',
      options: ['necessary', 'neccessary', 'necesary', 'neccesary'],
      correctAnswer: 'necessary',
    ),
    BankItem(
      id: 'sl_5',
      skill: SkillType.spelling,
      level: CefrLevel.b2,
      prompt: 'Which spelling is correct?',
      options: ['accommodate', 'acommodate', 'accomodate', 'acomodate'],
      correctAnswer: 'accommodate',
    ),
    BankItem(
      id: 'sl_6',
      skill: SkillType.spelling,
      level: CefrLevel.c1,
      prompt: 'Which spelling is correct?',
      options: ['conscientious', 'concientious', 'conscientous', 'consciencious'],
      correctAnswer: 'conscientious',
    ),
  ];
}

/// One item as stored in the bank, before it is projected to the client.
///
/// [correctAnswer] never leaves the server — the client receives shuffled
/// options only (rule R7).
class BankItem {
  const BankItem({
    required this.id,
    required this.skill,
    required this.level,
    required this.prompt,
    this.type = PlacementItemType.multipleChoice,
    this.options = const [],
    this.correctAnswer,
    this.passage,
    this.audioText,
    this.expectedWords = 0,
  });

  final String id;
  final SkillType skill;

  /// The CEFR band the item was authored for.
  final CefrLevel level;

  final PlacementItemType type;
  final String prompt;
  final List<String> options;
  final String? correctAnswer;
  final String? passage;
  final String? audioText;

  /// Rough production length a competent answer at this band reaches. Used only
  /// by the offline fallback scorer, never by the AI evaluator.
  final int expectedWords;

  bool get isFreeText => type == PlacementItemType.freeText;
}
