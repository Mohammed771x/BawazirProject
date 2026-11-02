import { AlertTriangle, Plus, Trash2 } from 'lucide-react'
import type { Evaluation as EvaluationData, MeasureDef, MeasuredMetric, MetricTarget } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { formatDate, formatValue } from '../lib/format'
import { KIND_LABEL, OUTCOME_LABEL } from '../lib/product'
import { Empty, Help } from './ui'
import { Link } from 'react-router-dom'

function Change({ m }: { m: MeasuredMetric }) {
  if (m.change === null) return <span className="muted">—</span>
  const sign = m.change > 0 ? '+' : ''
  const value = m.format === 'pct' ? `${sign}${(m.change * 100).toFixed(1)} نقطة` : `${sign}${formatValue(m.change, m.format)}`
  return <span className="ltr">{value}{m.relativeChange !== null && m.format !== 'pct' && <span className="muted"> ({sign}{(m.relativeChange * 100).toFixed(0)}%)</span>}</span>
}

function MetricRows({ rows, comparison }: { rows: MeasuredMetric[]; comparison: boolean }) {
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>المقياس</th><th className="n">قبل</th><th className="n">بعد</th><th className="n">التغير</th>
            {comparison && <th className="n" title="التغير لدى المجموعة المستهدفة ناقص التغير لدى مجموعة المقارنة">صافي مقارنةً بالمجموعة الأخرى</th>}
            <th>القراءة</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((m) => {
            const [label, tone] = OUTCOME_LABEL[m.outcome] ?? [m.outcome, '']
            return (
              <tr key={m.key}>
                <td>
                  <div style={{ fontWeight: 500 }}>{m.label}</div>
                  <div className="muted" style={{ fontSize: 11.5 }}>
                    {m.direction === 'up' ? 'الأفضل أن يرتفع' : 'الأفضل أن ينخفض'}
                    {m.target !== null && <> · الهدف {formatValue(m.target, m.format)}{m.targetMet !== null && (m.targetMet ? ' ✓' : ' ✗')}</>}
                  </div>
                </td>
                <td className="n">{formatValue(m.before, m.format)}<div className="muted" style={{ fontSize: 11 }}>n={m.beforeSample}</div></td>
                <td className="n">{formatValue(m.after, m.format)}<div className="muted" style={{ fontSize: 11 }}>n={m.afterSample}</div></td>
                <td className="n"><Change m={m} /></td>
                {comparison && <td className="n">{m.netOfComparison === null ? '—' : <span className="ltr">{m.format === 'pct' ? `${(m.netOfComparison * 100).toFixed(1)} نقطة` : formatValue(m.netOfComparison, m.format)}</span>}</td>}
                <td><span className={`badge ${tone}`}>{label}</span></td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

/**
 * Before against after, for one population (brief §12).
 *
 * Arithmetic only, and labelled as such: the cautions are fixed rules about
 * why the arithmetic may not mean what it seems to, and the verdict — whether
 * the change *worked* — is written by a person elsewhere on the page.
 */
export function EvaluationView({ e }: { e: EvaluationData }) {
  const comparison = !!e.comparisonPopulation
  return (
    <div className="stack lg">
      <div className="layer">
        <div className="layer-tag data">القياس — أرقام محسوبة، لا تفسير</div>
        <p style={{ margin: '4px 0 8px', fontWeight: 500 }}>{e.summary}</p>
        <div className="muted" style={{ fontSize: 12.5 }}>
          الفئة: {e.population} ({e.populationSize} متعلمًا)
          {comparison && <> · مقارنةً بـ: {e.comparisonPopulation}</>}
          {' · '}قبل: {formatDate(e.before.from)} — {formatDate(e.before.to)} ({e.before.days} يومًا)
          {' · '}بعد: {formatDate(e.after.from)} — {formatDate(e.after.to)}{!e.afterComplete && ' (لم تكتمل)'}
        </div>
      </div>
      {e.targeted.length === 0 ? <Empty title="لا مقاييس مستهدفة" text="حدد المقاييس التي يُفترض أن يحركها هذا القرار لتُقاس." /> : (
        <div className="card flush"><div className="card-head" style={{ padding: '18px 20px 0' }}><div><h2>المقاييس المستهدفة</h2></div></div><MetricRows rows={e.targeted} comparison={comparison} /></div>
      )}
      {e.guardrails.length > 0 && (
        <div className="card flush">
          <div className="card-head" style={{ padding: '18px 20px 0' }}><div><h2>ماذا تغير أيضًا؟</h2><p>مقاييس عامة تُعرض دائمًا — تحسن مقياس واحد لا يعني نجاح القرار</p></div></div>
          <MetricRows rows={e.guardrails} comparison={comparison} />
        </div>
      )}
      {(e.cautions.length > 0 || e.overlappingChanges.length > 0) && (
        <div className="layer">
          <div className="layer-tag hypo"><AlertTriangle size={13} /> قبل الاستنتاج</div>
          <ul>{e.cautions.map((c, i) => <li key={i}>{c}</li>)}</ul>
          {e.overlappingChanges.length > 0 && (
            <div className="stack" style={{ gap: 4 }}>
              <span className="muted" style={{ fontSize: 12.5 }}>تغييرات نُفذت في الفترة نفسها:</span>
              {e.overlappingChanges.map((o) => (
                <Link key={o.id} to={`/changes/${o.id}`} className="row" style={{ fontSize: 13 }}>
                  <span className="badge">{KIND_LABEL[o.kind] ?? o.kind}</span>{o.title}<span className="muted">· {formatDate(o.shippedOn)}</span>
                </Link>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  )
}

/**
 * Choose metrics from the dictionary — never type one: a metric outside the
 * dictionary cannot be measured, and the backend refuses it.
 */
export function MetricPicker({ value, onChange, catalogue }: {
  value: MetricTarget[]; onChange: (v: MetricTarget[]) => void; catalogue: MeasureDef[]
}) {
  const used = new Set(value.map((m) => m.key))
  const set = (i: number, patch: Partial<MetricTarget>) => onChange(value.map((m, j) => (j === i ? { ...m, ...patch } : m)))
  const byArea = catalogue.reduce<Record<string, MeasureDef[]>>((acc, m) => ((acc[m.area] ??= []).push(m), acc), {})
  return (
    <div className="stack">
      {value.map((m, i) => {
        const def = catalogue.find((d) => d.key === m.key)
        return (
          <div key={m.key} className="row wrap" style={{ gap: 6 }}>
            <span style={{ fontWeight: 500, minWidth: 180 }}>{def?.label ?? m.key}<Help text={def ? `${def.definition} — ${def.formula}` : null} /></span>
            <select className="select" value={m.direction} onChange={(e) => set(i, { direction: e.target.value as 'up' | 'down' })} aria-label="الاتجاه المطلوب">
              <option value="up">نريده أن يرتفع</option>
              <option value="down">نريده أن ينخفض</option>
            </select>
            <input className="input ltr" style={{ width: 110 }} type="number" step="any" placeholder={def?.format === 'pct' ? 'الهدف 0.25' : 'الهدف'} aria-label="الهدف"
              value={m.target ?? ''} onChange={(e) => set(i, { target: e.target.value === '' ? null : Number(e.target.value) })} />
            <button type="button" className="btn ghost sm" onClick={() => onChange(value.filter((_, j) => j !== i))} aria-label="حذف"><Trash2 /></button>
          </div>
        )
      })}
      {value.length < 8 && (
        <div className="row" style={{ gap: 6 }}>
          <Plus size={14} className="muted" />
          <select className="select" value="" aria-label="أضف مقياسًا" onChange={(e) => {
            const def = catalogue.find((d) => d.key === e.target.value)
            if (def) onChange([...value, { key: def.key, direction: def.goodWhenUp ? 'up' : 'down', target: null }])
          }}>
            <option value="">أضف مقياسًا من القاموس…</option>
            {Object.entries(byArea).map(([area, items]) => (
              <optgroup key={area} label={area}>
                {items.filter((d) => !used.has(d.key)).map((d) => <option key={d.key} value={d.key}>{d.label}</option>)}
              </optgroup>
            ))}
          </select>
        </div>
      )}
      <span className="muted" style={{ fontSize: 12 }}>النسب تُكتب كسور: 0.25 تعني 25%.</span>
    </div>
  )
}

/** A cohort, from the same list the global filter offers. Empty means everyone. */
export function SegmentSelect({ value, onChange, label, allowEmpty = 'كل المتعلمين' }: {
  value: string | null | undefined; onChange: (v: string | null) => void; label: string; allowEmpty?: string
}) {
  const { meta } = useAuth()
  const segments = (meta?.segments ?? []).filter((s) => s.key !== 'all')
  const levels = (meta?.levels ?? []).map((l) => ({ key: `level:${l}`, label: `المستوى ${l}` }))
  const all = [...segments, ...levels.filter((l) => !segments.some((s) => s.key === l.key))]
  return (
    <select className="select" aria-label={label} value={value ?? ''} onChange={(e) => onChange(e.target.value || null)}>
      <option value="">{allowEmpty}</option>
      {all.map((s) => <option key={s.key} value={s.key}>{s.label}</option>)}
    </select>
  )
}
