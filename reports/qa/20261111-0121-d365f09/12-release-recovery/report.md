# 12 — بناء الإصدار والنشر والنسخ الاحتياطي والاستعادة

## 1. الخلاصة

- **الحملة:** `20261111-0121-d365f09`، الجزء الثاني · النسخة: `d365f09` مع تعديلات ADR-137، وهي **التعديلات المحلية بدون commit**.
- **الإصدار المقصود:** APK تجريبي داخلي للطلاب (arm64)، وليس متجراً. والخادم على Render Free مع Neon Free.
- **ما نشرت ولا سويت commit أو migration على الإنتاج.**
- **النتيجة:**
  - البناء سليم، وما فيه أسرار في الـ APK ولا في حزمة لوحة الإدارة.
  - الـ migrations تنجح على قاعدة فارغة، وعلى لقطة بيانات من إصدار سابق، بدون أي تغيير في البيانات.
  - النسخ المشفّر والاستعادة لقاعدة معزولة نجحوا (RTO محلي 8 ثواني، والبيانات متطابقة).
  - **قبل التوزيع لازم ترفع رقم النسخة** (R12-1).
- **قرار النشر:** **لا مانع مثبت للتوزيع الداخلي.** المتجر **غير جاهز** (مفتاح debug)، وهذا معروف وموثق.

## 2. النتائج

### Artifact manifest

| العنصر | القيمة |
|---|---|
| المصدر | `d365f09` مع diff محلي: ADR-137، واختبارات QA، وإصلاح أداة الاختبار في `real_backend_journey_test.dart` |
| أمر البناء | `flutter build apk --release --split-per-abi --dart-define=WORDOS_MOCK=false --dart-define=WORDOS_API_BASE_URL=https://wordos-api.onrender.com/api` (91.6s، exit 0) |
| `app-arm64-v8a-release.apk` | 22,383,409 bytes · SHA-256 `e68efb81a1d13056e4790912b24b2ff589095a2bf42e68fdca4b8795111ca6e9` |
| رقم النسخة في الـ APK | **1.3.2** (`pubspec.yaml`: `1.3.2+6`) ← R12-1 |
| العنوان المضمّن | `https://wordos-api.onrender.com/api` فقط. ما فيه `5199` ولا عنوان LAN |
| التوقيع | v2، الشهادة `CN=Android Debug` (SHA-256 `7f4ac5df…a17b`) |
| الصلاحيات | INTERNET، RECORD_AUDIO، POST_NOTIFICATIONS، RECEIVE_BOOT_COMPLETED، VIBRATE، ACCESS_NETWORK_STATE |
| `usesCleartextTraffic` / `allowBackup` | غير مذكورين: cleartext ممنوع افتراضياً، و**`allowBackup` افتراضياً true** (ملاحظة من 04) |
| فحص الأسرار في الـ APK (مفاتيح Google وGroq وsk- وPEM وconnection strings) | تطابق واحد طلع **إنذار كاذب**: `package:wordos/app/wordos_app.dart` |
| لوحة الإدارة | `tsc -b` exit 0 · oxlint: 3 تحذيرات أسلوبية في `AiPage.tsx` · `vite build` ✓ (JS 486KB، gzip 139KB) · **ما فيه `VITE_ADMIN_MOCK` ولا `MockAdminApi` في الحزمة** · `npm audit --omit=dev`: 0 |
| الخادم | `dotnet build -c Release` 0 errors · الاختبارات: Domain 234/234، وAPI 586/588. الاثنين الفاشلين هم F06 وF10، ويثبتون D01-1 |
| ai-service | pytest 243/243 |
| mobile | `flutter analyze` نظيف · 574/574، ومعها `qa_ui_matrix_test` الجديد 76/76 |

### قاعدة البيانات

| الفحص | النتيجة | الدليل |
|---|---|---|
| migrations على قاعدة فارغة | 31 migration في 3.5s | جلسة `wordos_qa` |
| ترقية لقطة من `AdminIntelligence` (قبل آخر 3 migrations) فيها 508 مستخدم و1,607 كلمة و8,035 حالة مهارة و38,048 حدث و2,508 refresh token | 3 migrations في **3.0s** · **بصمات md5 للجداول الأساسية الثمانية متطابقة قبل وبعد** · الجداول الجديدة الأربعة موجودة | `upgrade-before.txt`، `upgrade-after.txt` |
| دور التشغيل `wordos_app` | `CREATE TABLE` ← permission denied · `ALTER`/`DROP` ← must be owner · ليس superuser ولا createdb ولا createrole · DML على 28/28 جدول، وUSAGE على 3/3 sequences | `db-roles.txt` |
| بدء الخدمة | ما يعتمد على دور المهاجر (الـ API اشتغل بـ `wordos_app` فقط) | |

**ملاحظة:** محلياً، الجداول الجديدة وصلت `wordos_app` تلقائياً. على Neon ما توصل (`docs/09-DEPLOYMENT.md` §grant)، والخطوة اليدوية مطلوبة لـ `product_decisions` و`product_changes` و`ai_conversations` و`ai_messages`، لو ما انعملت من قبل. **حالة Neon الفعلية غير مقروءة**.

### النسخ والاستعادة (محلياً، بيانات اصطناعية)

| الخطوة | القياس |
|---|---|
| آخر كتابة قبل النسخة | `word_events` 04:06:29 IST |
| `pg_dump -Fc` ثم `openssl aes-256-cbc -pbkdf2` بمفتاح عشوائي خارج المستودع | 2s، والحجم 25MB (القاموس 278MB فيها). الملف ما يُقرأ بدون المفتاح |
| فك التشفير ثم `pg_restore` إلى `wordos_qa_restore` المعزولة | **6s** |
| التحقق | بصمات الجداول الثمانية متطابقة. القاموس 434,561 = 434,561. الـ migrations 31. القيود 171 = 171 |
| **RPO / RTO محلي** | RPO = لحظة النسخة (يعني لو النسخ يومي، تضيع حتى 24 ساعة). **RTO حوالي 8s** للبيانات، بدون وقت النشر وإعادة توجيه الخدمة |

**Neon Free (المصدر: وثائق Neon، تاريخ القراءة 2026-11-11):**
- **الموجود:** استعادة فورية (PITR) لنافذة **6 ساعات**، وبحد **1GB** من سجل التغييرات. على الفرع الجذري فقط، وتُضبط من Settings ← Postgres.
- **غير المؤكد:** ما تأكدت من نسخ مجدولة طويلة الأجل على الخطة المجانية.
- **الخلاصة:** أي خطأ يُكتشف بعد 6 ساعات ما يُسترجع من Neon. والنسخة المحلية المشفرة **غير مجدولة حالياً**.

### الحاوية والمراقبة (مراجعة ثابتة)

| البند | النتيجة |
|---|---|
| المستخدم | `USER wordos` (uid 10001) ✓ |
| خدمة الذكاء الاصطناعي | على `127.0.0.1:8099` ✓ |
| موت أي عملية | `wait -n` يوقف الحاوية ✓ |
| الإيقاف | `trap TERM` يمرر SIGTERM للعمليتين ✓ |
| `ASPNETCORE_ENVIRONMENT` | Production ✓ |
| `HEALTHCHECK` | غير موجود في الـ Dockerfile، يعني يعتمد على إعداد Render |
| الصور الأساسية | `node:22-alpine` و`dotnet/aspnet:10.0` بالـ tag، مو بالـ digest، فالبناء قابل للإعادة جزئياً |
| ربط الخطأ بالسجل | رد الخطأ `{error:{code,message}}` **بدون traceId** (`Program.cs:470-505`)، فبلاغ الطالب ما يرتبط بسطر السجل إلا بالوقت. والسجل ما فيه أسرار (فحصت ثلاث سجلات تشغيل) |
| `/health/ready` | كاش ساعة (ADR-077)، فما يكشف انقطاع القاعدة لحظياً (انظر 07، O07-1) |

## 3. الملاحظات

| ID | الوصف | الشدة | قرار النشر | إجراء |
|---|---|---|---|---|
| **R12-1** | رقم النسخة لسا `1.3.2+6`، مع إن الإصدار القادم فيه ADR-135/136. نفس versionCode ما يرقّي فوق المثبّت بشكل موثوق، والطالب ما يميّز النسخة | — (خطوة إصدار) | **مطلوب قبل التوزيع** | `1.3.3+7` في `pubspec.yaml`، ثم البناء |
| O12-1 | ما فيه نسخ احتياطي مجدول خارج نافذة Neon (6 ساعات) | Medium (مخاطرة تشغيلية) | تحسين موصى به | `pg_dump` مشفّر يومي إلى تخزين خارجي، واستعادة تجريبية شهرية بنفس خطوات الجدول |
| O12-2 | ردود الأخطاء بدون معرّف طلب | Low | تحسين اختياري | `traceId = HttpContext.TraceIdentifier` في رد الخطأ وفي السجل |
| O12-3 | توقيع release بمفتاح debug | — | معروف (CLAUDE.md) | مفتاح رفع قبل المتجر. التغيير يجبر الطلاب على إلغاء التثبيت |

### خطة النشر والرجوع المقترحة

1. commit للتعديلات المحلية، بعد موافقة المالك.
2. **الخادم:** نشر على Render. ما فيه migration جديدة في ADR-136/137. لو `ai_conversations`/`product_*` موجودة من قبل على Neon، فما فيه grant جديد.
3. **التحقق بعد النشر:**
   - `/health/ready`.
   - دخول حساب تجريبي.
   - البحث يرجّع `alreadyAdded`.
   - طلب 429 يرجع `RATE_LIMITED` مع `Retry-After`.
4. **التطبيق:** رفع النسخة إلى 1.3.3+7، ثم بناء arm64، ثم التوزيع.
5. **الرجوع:**
   - **الخادم:** إعادة نشر commit السابق من Render، لأن ما فيه تغيير schema يمنع الرجوع.
   - **التطبيق 1.3.3:** يتوافق مع الخادم القديم. `alreadyAdded` اختياري (`?? false`)، و`RATE_LIMITED` معروف من قبل.

## 5. النواقص

| ما لم يُختبر | السبب | أقل إجراء |
|---|---|---|
| تثبيت جديد وترقية 1.3.2 إلى 1.3.3 مع بقاء الجلسة على جهاز | ما فيه جهاز Android | تثبيت يدوي على جوال تجريبي |
| iOS release وsigning | خارج النطاق (Android للطلاب) | |
| تشغيل الحاوية، cold start، الذاكرة المشتركة | ما فيه Docker | `docker build` و`docker run --memory 512m` |
| حالة migrations والـ grants على Neon، وإعداد نافذة الاستعادة الفعلي | ممنوع لمس الإنتاج في التقييم | قراءة من لوحة Neon |
| HTTPS والترويسات خلف Render، والروابط العميقة لـ `/admin` | ما فيه نشر | فحص بعد النشر |

## 6. الإعادة

```bash
cd mobile && flutter build apk --release --split-per-abi --dart-define=WORDOS_MOCK=false --dart-define=WORDOS_API_BASE_URL=https://wordos-api.onrender.com/api
cd admin-web && npm run typecheck && npm run lint && npm run build && npm audit --omit=dev
```

**النسخ والاستعادة:**
```bash
pg_dump -Fc wordos_qa | openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt -pass file:$KEY -out qa.dump.enc
openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -pass file:$KEY -in qa.dump.enc | pg_restore -d wordos_qa_restore --no-owner
```

**الترقية:** `dotnet ef database update 20261003212928_AdminIntelligence`، ثم البذر، ثم `dotnet ef database update`، ثم مقارنة `checksum.sql`. القواعد المؤقتة (`wordos_qa_upg`، `wordos_qa_restore`) انحذفت بعد القياس.

**المصادر:**
- [Neon — History window](https://neon.com/docs/postgres/backup-restore/history-window)
- [Neon — Backups](https://neon.com/docs/postgres/backup-restore/backups)
