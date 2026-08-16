/// Enumerations shared with the backend contract (`docs/05-API-CONTRACT.md`).
///
/// Wire values are the SCREAMING_SNAKE strings from `docs/04-DATA-MODEL.md`.
library;

T _parse<T>(Map<String, T> map, String? raw, T fallback) =>
    raw == null ? fallback : (map[raw] ?? fallback);

enum CefrLevel {
  a1('A1', 'A1'),
  a1Plus('A1_PLUS', 'A1+'),
  a2('A2', 'A2'),
  a2Plus('A2_PLUS', 'A2+'),
  b1('B1', 'B1'),
  b1Plus('B1_PLUS', 'B1+'),
  b2('B2', 'B2'),
  b2Plus('B2_PLUS', 'B2+'),
  c1('C1', 'C1'),
  c1Plus('C1_PLUS', 'C1+'),
  c2('C2', 'C2');

  const CefrLevel(this.wire, this.label);

  final String wire;
  final String label;

  static CefrLevel fromWire(String? raw) => _parse(
        {for (final v in CefrLevel.values) v.wire: v},
        raw,
        CefrLevel.a1,
      );

  /// Null-preserving variant, for skills that legitimately carry no band
  /// (Spelling — see ADR-008).
  static CefrLevel? tryFromWire(String? raw) =>
      raw == null ? null : {for (final v in CefrLevel.values) v.wire: v}[raw];

  /// Position on the ladder — used for ordering and comparisons only.
  int get rank => index;
}

enum SkillType {
  reading('READING'),
  listening('LISTENING'),
  speaking('SPEAKING'),
  writing('WRITING'),
  spelling('SPELLING');

  const SkillType(this.wire);

  final String wire;

  static SkillType fromWire(String? raw) => _parse(
        {for (final v in SkillType.values) v.wire: v},
        raw,
        SkillType.reading,
      );
}

enum SkillStatus {
  pending('PENDING'),
  available('AVAILABLE'),
  passed('PASSED'),
  failed('FAILED');

  const SkillStatus(this.wire);

  final String wire;

  static SkillStatus fromWire(String? raw) => _parse(
        {for (final v in SkillStatus.values) v.wire: v},
        raw,
        SkillStatus.pending,
      );
}

enum WordState {
  learning('LEARNING'),
  mature('MATURE'),
  active('ACTIVE'),
  archived('ARCHIVED');

  const WordState(this.wire);

  final String wire;

  static WordState fromWire(String? raw) => _parse(
        {for (final v in WordState.values) v.wire: v},
        raw,
        WordState.learning,
      );
}

/// Server-decided availability of a skill in the hub. The client never derives it.
enum SkillAvailability {
  available('AVAILABLE'),
  empty('EMPTY'),
  locked('LOCKED');

  const SkillAvailability(this.wire);

  final String wire;

  static SkillAvailability fromWire(String? raw) => _parse(
        {for (final v in SkillAvailability.values) v.wire: v},
        raw,
        SkillAvailability.empty,
      );
}

enum OnboardingStage {
  interests('INTERESTS'),
  placement('PLACEMENT'),
  complete('COMPLETE');

  const OnboardingStage(this.wire);

  final String wire;

  static OnboardingStage fromWire(String? raw) => _parse(
        {for (final v in OnboardingStage.values) v.wire: v},
        raw,
        OnboardingStage.interests,
      );
}

enum UserRole {
  user('USER'),
  owner('OWNER');

  const UserRole(this.wire);

  final String wire;

  static UserRole fromWire(String? raw) =>
      _parse({for (final v in UserRole.values) v.wire: v}, raw, UserRole.user);
}

enum SessionItemType {
  comprehension('COMPREHENSION'),
  targetWord('TARGET_WORD'),
  writingTask('WRITING_TASK'),
  speakingTurn('SPEAKING_TURN'),
  spellingTask('SPELLING_TASK'),
  reviewItem('REVIEW_ITEM');

  const SessionItemType(this.wire);

  final String wire;

  static SessionItemType fromWire(String? raw) => _parse(
        {for (final v in SessionItemType.values) v.wire: v},
        raw,
        SessionItemType.comprehension,
      );
}

/// What kind of hint a spelling task shows — chosen server-side from the level.
enum SpellingClueKind {
  arabicMeaning('ARABIC_MEANING'),
  definitionEn('DEFINITION_EN'),
  synonym('SYNONYM');

  const SpellingClueKind(this.wire);

  final String wire;

  static SpellingClueKind fromWire(String? raw) => _parse(
        {for (final v in SpellingClueKind.values) v.wire: v},
        raw,
        SpellingClueKind.arabicMeaning,
      );
}

enum SpellingInputMode {
  letterTiles('LETTER_TILES'),
  freeTyping('FREE_TYPING');

  const SpellingInputMode(this.wire);

  final String wire;

  static SpellingInputMode fromWire(String? raw) => _parse(
        {for (final v in SpellingInputMode.values) v.wire: v},
        raw,
        SpellingInputMode.letterTiles,
      );
}

enum PlacementItemType {
  multipleChoice('MULTIPLE_CHOICE'),
  freeText('FREE_TEXT');

  const PlacementItemType(this.wire);

  final String wire;

  static PlacementItemType fromWire(String? raw) => _parse(
        {for (final v in PlacementItemType.values) v.wire: v},
        raw,
        PlacementItemType.multipleChoice,
      );
}

/// Why a level moved. Only `SYSTEM_VALIDATED_CHANGE` may drive progression and
/// archiving (rule R6).
enum LevelChangeType {
  placement('PLACEMENT'),
  userManualChange('USER_MANUAL_CHANGE'),
  systemValidated('SYSTEM_VALIDATED_CHANGE');

  const LevelChangeType(this.wire);

  final String wire;

  static LevelChangeType fromWire(String? raw) => _parse(
        {for (final v in LevelChangeType.values) v.wire: v},
        raw,
        LevelChangeType.placement,
      );
}

enum WordEventType {
  added('ADDED'),
  skillStarted('SKILL_STARTED'),
  skillPassed('SKILL_PASSED'),
  skillFailed('SKILL_FAILED'),
  becameMature('BECAME_MATURE'),
  enteredActive('ENTERED_ACTIVE'),
  exposureIncremented('EXPOSURE_INCREMENTED'),
  archived('ARCHIVED');

  const WordEventType(this.wire);

  final String wire;

  static WordEventType fromWire(String? raw) => _parse(
        {for (final v in WordEventType.values) v.wire: v},
        raw,
        WordEventType.added,
      );
}
