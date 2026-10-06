import { Table2 } from 'lucide-react'
import { useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import type { ChartSpec, Format, Skill } from '../../api/types'
import { formatValue } from '../../lib/format'

// One chart system for the whole site (see the dataviz method): thin marks,
// recessive grid, one y-axis, a hover layer on everything, a legend whenever
// there are two series or more, and a table view for every chart.

export const SKILL_COLOR: Record<Skill, string> = {
  READING: 'var(--s-reading)',
  LISTENING: 'var(--s-listening)',
  SPEAKING: 'var(--s-speaking)',
  WRITING: 'var(--s-writing)',
  SPELLING: 'var(--s-spelling)',
}

const SERIES = ['var(--series-1)', 'var(--series-2)', 'var(--series-3)', 'var(--s-writing)', 'var(--s-speaking)']
export const seriesColor = (i: number) => SERIES[Math.min(i, SERIES.length - 1)]

export interface Series { name: string; values: (number | null)[]; color?: string }

function useWidth<T extends HTMLElement>() {
  const ref = useRef<T>(null)
  const [width, setWidth] = useState(600)
  useLayoutEffect(() => {
    if (!ref.current) return
    const ro = new ResizeObserver(([e]) => setWidth(Math.max(240, e.contentRect.width)))
    ro.observe(ref.current)
    return () => ro.disconnect()
  }, [])
  return [ref, width] as const
}

/** Clean axis ticks: 0, a round step, never more than five lines. */
function niceTicks(max: number, unit: Format): number[] {
  if (unit === 'pct') {
    const top = max > 0.5 ? 1 : max > 0.25 ? 0.5 : max > 0.1 ? 0.25 : 0.1
    return [0, top / 2, top]
  }
  if (max <= 0) return [0, 1]
  const raw = max / 4
  const mag = 10 ** Math.floor(Math.log10(raw))
  const step = [1, 2, 2.5, 5, 10].map((m) => m * mag).find((s) => s >= raw) ?? raw
  const ticks: number[] = []
  for (let v = 0; v <= max + step * 0.001; v += step) ticks.push(v)
  if (ticks[ticks.length - 1] < max) ticks.push(ticks[ticks.length - 1] + step)
  return ticks
}

const axisLabel = (v: number, unit: Format) =>
  unit === 'pct' ? `${Math.round(v * 100)}%` : unit === 'ms' ? formatValue(v, 'ms') : formatValue(v, 'int', true)

export function Legend({ series }: { series: Series[] }) {
  if (series.length < 2) return null
  return (
    <div className="legend">
      {series.map((s, i) => (
        <span key={s.name}><i style={{ background: s.color ?? seriesColor(i) }} />{s.name}</span>
      ))}
    </div>
  )
}

function Tooltip({ x, y, width, children }: { x: number; y: number; width: number; children: ReactNode }) {
  const left = Math.min(Math.max(x + 12, 0), width - 170)
  return <div className="chart-tooltip" style={{ left, top: Math.max(0, y - 10) }}>{children}</div>
}

function TooltipRows({ title, rows, unit }: { title: string; rows: { name: string; color: string; value: number | null }[]; unit: Format }) {
  return (
    <>
      <div className="t-title">{title}</div>
      {rows.map((r) => (
        <div className="t-row" key={r.name}><span className="dot" style={{ background: r.color }} />{r.name}<b>{formatValue(r.value, unit)}</b></div>
      ))}
    </>
  )
}

// ── Line ────────────────────────────────────────────────────────────────────

export function LineChart({ labels, series, unit = 'int', height = 220, area = true }: {
  labels: string[]; series: Series[]; unit?: Format; height?: number; area?: boolean
}) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const [hover, setHover] = useState<number | null>(null)
  const pad = { t: 10, r: 12, b: 26, l: 44 }
  const w = width - pad.l - pad.r
  const h = height - pad.t - pad.b
  const all = series.flatMap((s) => s.values).filter((v): v is number => v !== null)
  const ticks = niceTicks(Math.max(0, ...all), unit)
  const top = ticks[ticks.length - 1] || 1
  const x = (i: number) => pad.l + (labels.length <= 1 ? w / 2 : (i / (labels.length - 1)) * w)
  const y = (v: number) => pad.t + h - (v / top) * h
  const every = Math.max(1, Math.ceil(labels.length / Math.max(2, Math.floor(w / 70))))

  const path = (values: (number | null)[]) => {
    let d = ''
    let pen = false
    values.forEach((v, i) => {
      if (v === null) { pen = false; return }
      d += `${pen ? 'L' : 'M'}${x(i).toFixed(1)},${y(v).toFixed(1)}`
      pen = true
    })
    return d
  }

  const onMove = (e: React.MouseEvent<SVGRectElement>) => {
    const box = e.currentTarget.getBoundingClientRect()
    const rel = (e.clientX - box.left) / box.width
    setHover(Math.round(rel * (labels.length - 1)))
  }

  return (
    <div className="chart" ref={ref}>
      <svg width={width} height={height} role="img" aria-label={series.map((s) => s.name).join('، ')}>
        {ticks.map((t) => (
          <g key={t}>
            <line className="grid-line" x1={pad.l} x2={width - pad.r} y1={y(t)} y2={y(t)} />
            <text x={pad.l - 8} y={y(t) + 4} textAnchor="end">{axisLabel(t, unit)}</text>
          </g>
        ))}
        {labels.map((l, i) => ((i % every === 0 && labels.length - 1 - i >= every / 2) || i === labels.length - 1) && (
          <text key={i} x={x(i)} y={height - 6} textAnchor="middle">{l}</text>
        ))}
        {series.map((s, si) => {
          const color = s.color ?? seriesColor(si)
          const d = path(s.values)
          const lastIdx = s.values.map((v, i) => (v === null ? -1 : i)).filter((i) => i >= 0).pop()
          return (
            <g key={s.name}>
              {area && series.length === 1 && d && lastIdx !== undefined && (
                <path d={`${d}L${x(lastIdx)},${y(0)}L${x(s.values.findIndex((v) => v !== null))},${y(0)}Z`} fill={color} opacity={0.1} />
              )}
              <path d={d} fill="none" stroke={color} strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
              {lastIdx !== undefined && <circle cx={x(lastIdx)} cy={y(s.values[lastIdx]!)} r={4} fill={color} stroke="var(--surface)" strokeWidth={2} />}
            </g>
          )
        })}
        {hover !== null && (
          <g>
            <line x1={x(hover)} x2={x(hover)} y1={pad.t} y2={pad.t + h} stroke="var(--border-strong)" />
            {series.map((s, si) => s.values[hover] !== null && (
              <circle key={s.name} cx={x(hover)} cy={y(s.values[hover]!)} r={4.5} fill={s.color ?? seriesColor(si)} stroke="var(--surface)" strokeWidth={2} />
            ))}
          </g>
        )}
        <rect x={pad.l} y={pad.t} width={w} height={h} fill="transparent" onMouseMove={onMove} onMouseLeave={() => setHover(null)} />
      </svg>
      {hover !== null && (
        <Tooltip x={x(hover)} y={pad.t} width={width}>
          <TooltipRows title={labels[hover]} unit={unit} rows={series.map((s, si) => ({ name: s.name, color: s.color ?? seriesColor(si), value: s.values[hover] }))} />
        </Tooltip>
      )}
    </div>
  )
}

// ── Columns ─────────────────────────────────────────────────────────────────

export function BarChart({ labels, series, unit = 'int', height = 220, onSelect, stacked = false }: {
  labels: string[]; series: Series[]; unit?: Format; height?: number; onSelect?: (index: number) => void; stacked?: boolean
}) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const [hover, setHover] = useState<number | null>(null)
  const pad = { t: 10, r: 8, b: 30, l: 44 }
  const w = width - pad.l - pad.r
  const h = height - pad.t - pad.b
  const totals = labels.map((_, i) => series.reduce((s, x) => s + (x.values[i] ?? 0), 0))
  const max = stacked ? Math.max(0, ...totals) : Math.max(0, ...series.flatMap((s) => s.values.map((v) => v ?? 0)))
  const ticks = niceTicks(max, unit)
  const top = ticks[ticks.length - 1] || 1
  const band = w / Math.max(1, labels.length)
  const groupCount = stacked ? 1 : series.length
  const barW = Math.min(24, (band * 0.7 - (groupCount - 1) * 2) / groupCount)
  const y = (v: number) => pad.t + h - (v / top) * h
  const every = Math.max(1, Math.ceil(labels.length / Math.max(2, Math.floor(w / 64))))

  // A 4px rounded data end, square at the baseline.
  const bar = (bx: number, by: number, bw: number, bh: number, color: string, key: string, rounded = true) => {
    if (bh <= 0) return null
    const r = rounded ? Math.min(4, bh, bw / 2) : 0
    return (
      <path key={key} fill={color}
        d={`M${bx},${by + bh}V${by + r}Q${bx},${by} ${bx + r},${by}H${bx + bw - r}Q${bx + bw},${by} ${bx + bw},${by + r}V${by + bh}Z`} />
    )
  }

  return (
    <div className="chart" ref={ref}>
      <svg width={width} height={height} role="img">
        {ticks.map((t) => (
          <g key={t}>
            <line className="grid-line" x1={pad.l} x2={width - pad.r} y1={y(t)} y2={y(t)} />
            <text x={pad.l - 8} y={y(t) + 4} textAnchor="end">{axisLabel(t, unit)}</text>
          </g>
        ))}
        {labels.map((label, i) => {
          const cx = pad.l + band * i + band / 2
          const groupW = stacked ? barW : groupCount * barW + (groupCount - 1) * 2
          let acc = 0
          return (
            <g key={i} opacity={hover === null || hover === i ? 1 : 0.55}>
              {series.map((s, si) => {
                const v = s.values[i]
                if (v === null || v === undefined) return null
                const color = s.color ?? seriesColor(si)
                if (stacked) {
                  const yTop = y(acc + v)
                  const bh = y(acc) - yTop - (acc > 0 ? 2 : 0)
                  acc += v
                  return bar(cx - barW / 2, yTop, barW, bh, color, s.name, si === series.length - 1)
                }
                const bx = cx - groupW / 2 + si * (barW + 2)
                return bar(bx, y(v), barW, y(0) - y(v), color, s.name)
              })}
              {(i % every === 0) && (
                <text x={cx} y={height - 10} textAnchor="middle">{label.length > 14 ? `${label.slice(0, 13)}…` : label}</text>
              )}
              <rect x={pad.l + band * i} y={pad.t} width={band} height={h} fill="transparent"
                style={{ cursor: onSelect ? 'pointer' : 'default' }}
                onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)} onClick={() => onSelect?.(i)} />
            </g>
          )
        })}
        <line className="axis-line" x1={pad.l} x2={width - pad.r} y1={y(0)} y2={y(0)} />
      </svg>
      {hover !== null && (
        <Tooltip x={pad.l + band * hover + band / 2} y={pad.t} width={width}>
          <TooltipRows title={labels[hover]} unit={unit} rows={series.map((s, si) => ({ name: s.name, color: s.color ?? seriesColor(si), value: s.values[hover] }))} />
        </Tooltip>
      )}
    </div>
  )
}

// ── Horizontal bars (RTL-native, for ranked categories with long labels) ───

export function HBars({ items, unit = 'int', max }: {
  items: { label: string; value: number | null; color?: string; sub?: string; onClick?: () => void }[]
  unit?: Format; max?: number
}) {
  const top = max ?? Math.max(0, ...items.map((i) => i.value ?? 0))
  return (
    <div className="hbars">
      {items.map((i) => (
        <div key={i.label} className={`hbar ${i.onClick ? 'clickable' : ''}`} onClick={i.onClick} title={i.sub}>
          <span className="hbar-label">{i.label}</span>
          <span className="hbar-track">
            <span className="hbar-fill" style={{ width: `${top > 0 ? ((i.value ?? 0) / top) * 100 : 0}%`, background: i.color ?? 'var(--series-1)' }} />
          </span>
          <span className="hbar-value">{formatValue(i.value, unit)}</span>
        </div>
      ))}
    </div>
  )
}

// ── Donut (parts of a whole, ≤ 5 parts) ─────────────────────────────────────

export function Donut({ items, size = 168, center }: {
  items: { label: string; value: number; color: string }[]; size?: number; center?: ReactNode
}) {
  const [hover, setHover] = useState<number | null>(null)
  const total = items.reduce((s, i) => s + i.value, 0)
  const r = size / 2 - 10
  const c = 2 * Math.PI * r
  let offset = 0
  return (
    <div className="row" style={{ gap: 20, flexWrap: 'wrap' }}>
      <div style={{ position: 'relative', width: size, height: size }}>
        <svg width={size} height={size} style={{ transform: 'rotate(-90deg)' }}>
          <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke="var(--surface-2)" strokeWidth={16} />
          {total > 0 && items.map((it, i) => {
            const len = (it.value / total) * c
            const seg = (
              <circle key={it.label} cx={size / 2} cy={size / 2} r={r} fill="none" stroke={it.color} strokeWidth={hover === i ? 20 : 16}
                strokeDasharray={`${Math.max(0, len - 2)} ${c}`} strokeDashoffset={-offset}
                onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)} style={{ transition: 'stroke-width .1s' }} />
            )
            offset += len
            return seg
          })}
        </svg>
        <div style={{ position: 'absolute', inset: 0, display: 'grid', placeItems: 'center', textAlign: 'center' }}>
          {hover !== null
            ? <div><div style={{ fontSize: 20, fontWeight: 600 }}>{formatValue(items[hover].value / total, 'pct')}</div><div className="muted" style={{ fontSize: 12 }}>{items[hover].label}</div></div>
            : center ?? <div><div style={{ fontSize: 20, fontWeight: 600 }}>{formatValue(total, 'int')}</div><div className="muted" style={{ fontSize: 12 }}>الإجمالي</div></div>}
        </div>
      </div>
      <div className="stack" style={{ gap: 6, flex: 1, minWidth: 140 }}>
        {items.map((it, i) => (
          <div key={it.label} className="row between" style={{ fontSize: 13 }} onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)}>
            <span className="row"><span className="dot" style={{ background: it.color }} />{it.label}</span>
            <span className="num"><b>{formatValue(it.value, 'int')}</b> <span className="muted">· {formatValue(total ? it.value / total : null, 'pct')}</span></span>
          </div>
        ))}
      </div>
    </div>
  )
}

// ── Table view (every chart has one) ────────────────────────────────────────

export function DataTable({ labels, series, unit }: { labels: string[]; series: Series[]; unit: Format }) {
  return (
    <div className="table-wrap" style={{ maxHeight: 280, overflowY: 'auto' }}>
      <table className="table">
        <thead><tr><th></th>{series.map((s) => <th key={s.name} className="n">{s.name}</th>)}</tr></thead>
        <tbody>
          {labels.map((l, i) => (
            <tr key={l + i}><td>{l}</td>{series.map((s) => <td key={s.name} className="n">{formatValue(s.values[i], unit)}</td>)}</tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/**
 * A chart with its legend and a table toggle.
 */
export function ChartFrame({ labels, series, unit, children, note }: {
  labels: string[]; series: Series[]; unit: Format; children: ReactNode; note?: ReactNode
}) {
  const [table, setTable] = useState(false)
  return (
    <div>
      {table ? <DataTable labels={labels} series={series} unit={unit} /> : children}
      <div className="chart-foot">
        <Legend series={series} />
        <button className="btn ghost sm" onClick={() => setTable((t) => !t)} aria-pressed={table} style={{ marginInlineStart: 'auto' }}>
          <Table2 /> {table ? 'عرض كرسم' : 'عرض كجدول'}
        </button>
      </div>
      {note && <div className="chart-note">{note}</div>}
    </div>
  )
}

/** Renders a backend `ChartSpec` (investigations) with the right form. */
export function SpecChart({ spec }: { spec: ChartSpec }) {
  const series: Series[] = spec.series.map((s, i) => ({ ...s, color: seriesColor(i) }))
  if (spec.labels.length === 0 || series.every((s) => s.values.every((v) => v === null))) {
    return <div className="muted" style={{ padding: 20, textAlign: 'center' }}>لا توجد بيانات كافية لهذا الرسم.</div>
  }
  let body: ReactNode
  if (spec.type === 'line') body = <LineChart labels={spec.labels} series={series} unit={spec.unit} />
  else if (spec.type === 'donut') {
    const colors = ['var(--series-1)', 'var(--s-writing)', 'var(--s-speaking)', 'var(--series-3)', 'var(--text-3)']
    body = <Donut items={spec.labels.map((l, i) => ({ label: l, value: series[0].values[i] ?? 0, color: colors[i % colors.length] }))} />
  } else if (spec.type === 'funnel' || (series.length === 1 && spec.labels.some((l) => l.length > 10))) {
    body = <HBars unit={spec.unit} items={spec.labels.map((l, i) => ({ label: l, value: series[0].values[i] }))} />
    if (series.length > 1) body = <BarChart labels={spec.labels} series={series} unit={spec.unit} />
  } else body = <BarChart labels={spec.labels} series={series} unit={spec.unit} />
  return <ChartFrame labels={spec.labels} series={series} unit={spec.unit} note={spec.note}>{body}</ChartFrame>
}
