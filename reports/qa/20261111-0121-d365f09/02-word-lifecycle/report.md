# 02 — قواعد التعلم ودورة الكلمة والمستويات

## 1. الخلاصة

- **الحملة:** `20261111-0121-d365f09` · 2026-11-11 · توقيت الجهاز IST (+05:30)
- **النسخة:** HEAD `d365f09`. الـ diff المحلي وقت البدء: `docs/02-PROGRESS.md` و`docs/README.md` و`.DS_Store` فقط (تعديلات جلسة أخرى، لا تمس الكود)، وبصمته `b6c17e94378a6bf8`.
- **البيئة:** محلي على macOS 25.2، .NET 10.0.400، وPostgreSQL 17.11 بقاعدة مؤقتة تُبنى من الـ migrations (`PostgresFixture`). الذكاء الاصطناعي والساعة بدائل حتمية (`ApiFactory`).
- **النتيجة:** **اجتاز النطاق المنفذ.** لم تظهر أي مخالفة لقاعدة من قواعد الخط.
- **ما ثبت:**
  - حدود `AvailableAt` بدقة tick: قبل الموعد بـ tick مغلق، وعنده وبعده مفتوح.
  - الفشل يعيد المهارة نفسها بعد الفجوة فقط.
  - 800 مسار عشوائي (4 بذور × 200 كلمة) حافظت على كل الثوابت: ترتيب المهارات الخمس، وعدم إرجاع أي مهارة مجتازة (R5)، و5 صفوف مهارة دائماً، وحدث نضج واحد وحدث Active واحد.
  - عتبات المستوى عند 0.85 / 0.8499 / 0.70 / 0.6999، و13 مقابل 14 جلسة.
  - شروط الأرشفة كل شرط وحده: فرق 4 درجات، والظهور 3 مرات، وحالة Active، وأضعف مهارة مثبتة لا المختارة.
  - المراجعة الأسبوعية: النضج بعد 7 أيام بالضبط، وإعادة الكلمة الخاطئة بعد 7 أيام من المراجعة، وسقف 50 كلمة، وأنها لا تلمس حالة الخط.
- **ما لم يثبت:**
  - تجربة تغيير ترتيب المهارات على قاعدة حقيقية فيها كلمات قديمة. مغطاة على مستوى الـ Domain فقط (`A_word_in_flight_when_the_order_changes_still_owes_every_skill`).
  - اختبار تحديد المستوى على البنك الحقيقي من البداية للنهاية: أعدت استخدام الاختبارات الموجودة ولم أضف عليها.
- **قرار النشر لهذا النطاق:** لا مانع مثبت.

## 2. النطاق والنتائج

| المتطلب | الاختبار | الطبقة | المتوقع ومصدره | النتيجة |
|---|---|---|---|---|
| أول إضافة تفتح Reading فقط | `WordPipelineTests.A_new_word_enters_the_pipeline_at_the_first_skill_only` | Domain | ترتيب `SkillsOrder` | PASS |
| حد الإتاحة بدقة tick | `Qa/LifecycleBoundaryQaTests.Next_skill_opens_exactly_at_the_gap_not_one_tick_before` | Domain | `SkillIntervalDays=2` | PASS |
| إعادة المهارة الراسبة بعد الفجوة | `…A_failed_skill_reopens_at_the_gap_from_the_failure` | Domain | R5، `Word.ApplySessionResult` | PASS |
| فجوة مخصصة (5 أيام) | `…A_custom_gap_is_obeyed` | Domain | R3 | PASS |
| تسلسلات عشوائية، 4 بذور × 200 | `…Random_pass_fail_sequences_keep_every_invariant` | Domain (property) | R5، والثوابت 1–3 في `04-DATA-MODEL` | PASS ×4 |
| كلمة Active ترفض نتيجة جديدة | `…An_active_word_refuses_any_further_session_result` | Domain | دورة الحياة | PASS |
| المحذوفة لا تؤهَّل ولا تُراجَع | `…A_deleted_word_takes_no_session_result_and_is_not_eligible` | Domain | ADR-071 | PASS |
| تغيير المعنى لا يحرك الرحلة | `…Changing_the_meaning_moves_nothing_in_the_journey` | Domain | ADR-101 | PASS |
| 13 جلسة لا تقرر و14 تقرر | `…Thirteen_sessions_decide_nothing_and_fourteen_do` | Domain | `MinEvaluationSessions=14` | PASS |
| حواف العتبات (5 حالات) | `…Threshold_edges_follow_the_configuration` | Domain | 0.85 و0.70 مع epsilon | PASS ×5 |
| Spelling بلا مستوى | `…Spelling_never_carries_or_moves_a_level` | Domain | ADR-008 | PASS |
| الأرشفة: كل شرط وحده (4 حالات) | `…Archive_needs_both_the_gap_and_the_exposure` | Domain | ADR-013 | PASS ×4 |
| الأرشفة من Active فقط (3 حالات) | `…Only_an_active_word_is_ever_archived` | Domain | ADR-013 | PASS ×3 |
| الأضعف يقرر لا المختار | `…The_weakest_validated_skill_decides_and_the_user_selected_level_does_not` | Domain | R6، ADR-013 | PASS |
| الأرشفة تحفظ السجل | `…Archiving_keeps_the_row_and_its_history` | Domain | R8 | PASS |
| نضج المراجعة بعد 7 أيام بالضبط | `…A_word_ripens_exactly_seven_days_after_it_was_added` | Domain | ADR-089 | PASS |
| الخاطئة تعود بعد 7 أيام، والنجاح لا يُسحب | `…A_missed_word_ripens_seven_days_after_the_review_that_missed_it` | Domain | ADR-099 | PASS |
| سقف 50 والأقدم أولاً | `…A_sitting_is_capped_at_fifty_oldest_first_and_the_rest_stay_ripe` | Domain | `WeeklyReviewMaxWords` | PASS |
| المراجعة تغيّر حقولها فقط | `…Reviewing_touches_only_the_review_fields` | Domain | R9 | PASS |
| R5 على API وقاعدة حقيقية | `SessionTests.Failing_a_later_skill_never_resets_the_earlier_ones` | API+PG | R5 | PASS |
| نضج الكلمة عبر الخمس مهارات | `SessionTests.Passing_the_last_skill_matures_the_word_into_active_vocabulary` | API+PG | §5 | PASS |
| المراجعة لا تغيّر شيئاً | `SessionTests.The_weekly_review_measures_without_changing_anything` | API+PG | R9 | PASS |
| حذف وإعادة إضافة رحلة جديدة | `WordOwnershipTests.A_deleted_word_can_be_added_again_as_a_new_journey` | API+PG | ADR-071 | PASS |
| Placement: 8 أسئلة، البدء السهل، عدم التصعيد بعد الخطأ | `PlacementAlgorithmTests` (24 اختبار) | Domain | ADR-027/098 | PASS |
| الـ warm-up لا يغيّر شيئاً | `SessionTests.A_missed_warmup_word_changes_nothing_at_all` | API+PG | §26 | PASS |

**الإجمالي للاختبارات الجديدة:** planned 30؛ PASS 30؛ FAIL 0؛ BLOCKED 0؛ NOT_RUN 0؛ skipped 0. ملف TRX في `raw/`.
**الاختبارات الموجودة المعاد استخدامها:** كلها PASS ضمن التشغيل الكامل (انظر `../README.md`).

### جدول R1–R9

| القاعدة | الدليل | الأثر في القاعدة | النتيجة |
|---|---|---|---|
| R1 لا منطق في Flutter | خارج نطاق هذا التخصص. فحص ADR-136: علامة «مضافة» من الخادم | — | N/A هنا |
| R2 الذكاء الاصطناعي لا يقرر | `SessionTests.A_grammar_slip_does_not_fail_correct_usage` و`A_sentence_without_the_word_does_not_pass` | `word_skill_states` يحددها الخادم من الملاحظات | PASS |
| R3 الإعدادات | `A_custom_gap_is_obeyed`، `The_ceiling_is_configuration_and_not_a_constant` | المواعيد تتبع الإعداد | PASS |
| R5 | المسار العشوائي + API | لا رجوع عن `Passed` | PASS |
| R6 | `The_weakest_validated_skill_decides…`، `Applying_a_decision_never_touches_the_user_selected_level` | `user_selected_level` لا يقود الأرشفة | PASS |
| R7 الخلط على الخادم | `SessionTests.The_correct_answer_is_never_sent_with_the_question`، و`Qa/SecurityQaTests.S06` | لا مفتاح إجابة في الرد | PASS |
| R8 الظهور أولوية فقط | `Exposure_is_recorded_but_never_removes_a_word`، `ExposureTests` | لا حذف بسبب الظهور | PASS |
| R9 المراجعة تقيس | `Reviewing_touches_only_the_review_fields` + API | تتغير `LastReviewedAt/ReviewPassedAt` فقط | PASS |

## 3. العيوب والملاحظات المثبتة

**لم تظهر عيوب ضمن الاختبارات المنفذة.**

### يحتاج تحققاً أو قراراً من المالك (ليس عيباً مثبتاً)

| ID | الملاحظة | الدليل |
|---|---|---|
| Q02-1 | **الأرشفة تُفحص فقط لحظة الترقية.** `SessionEndpoints.cs:1546–1555` يستدعي `ArchiveOutgrown` داخل `if (decision.IsPromotion)` فقط. الكلمة التي صارت مؤهلة **بعد** آخر ترقية (مثلاً وصل ظهورها إلى 3 لاحقاً، أو دخلت Active بعد الترقية) تبقى Active حتى الترقية القادمة، وقد لا تجيء. الوثائق تقول «may be archived» ولا تحدد متى يُفحص. ليس خرقاً لنص مكتوب، لكنه يؤخر الأرشفة. | قراءة الكود |
| Q02-2 | **الوثائق تقول «فوق 85%»** (`08-FINAL-SPECIFICATION.md` §7)، **والكود يرقّي عند `>= 0.85`** مع epsilon. الكود والاختبار متفقان. التعارض في الصياغة فقط. | `LevelEngine.cs:51` |
| Q02-3 | **النافذة تراكمية لا متدحرجة.** `RollingAccuracy` هو متوسط كل الجلسات منذ آخر قرار، وكل 14 جلسة تُصفّر النافذة حتى في حالة Hold. السلوك متسق مع ADR-013، لكن الاسم «rolling» مضلل. | `SkillLevel.cs:49`، `LevelEngine.Apply` |
| Q02-4 | **دقة Reading/Listening للمستوى تُحسب من أسئلة الفهم فقط**، لا من أسئلة الكلمات. موثق في التعليق، ويستحق ذكره في المواصفات. | `SessionEndpoints.cs:1510–1516` |

## 5. النواقص

| ما لم يُختبر | السبب | الأثر | أقل إجراء |
|---|---|---|---|
| دورة 14 جلسة عبر API تنتهي بترقية وأرشفة فعلية | مكلفة. مغطاة على مستوى الـ Domain | منخفض | اختبار API واحد يحقن 14 جلسة |
| تغيير `SkillsOrder` على بيانات حقيقية قديمة | يحتاج بيانات إنتاج | منخفض | تشغيل على نسخة من القاعدة |

## 6. إعادة التشغيل

```bash
cd backend && dotnet test tests/WordOs.Domain.Tests --filter "FullyQualifiedName~Qa"
```
الملف: `backend/tests/WordOs.Domain.Tests/Qa/LifecycleBoundaryQaTests.cs`. البذور: 20261111، 1، 42، 987654.
