import { Fragment, type ReactNode } from 'react'

/**
 * The little Markdown WordOS AI writes — headings, paragraphs, bold, inline
 * code, bulleted and numbered lists, and tables — rendered as React elements.
 *
 * Never as HTML: the text comes from a model, and a model's output is not
 * trusted markup. Anything this does not recognise is shown as plain text.
 */
export function Markdown({ text }: { text: string }) {
  const lines = text.replace(/\r\n/g, '\n').split('\n')
  const blocks: ReactNode[] = []
  let i = 0
  let key = 0

  while (i < lines.length) {
    const line = lines[i]
    if (!line.trim()) { i++; continue }

    const heading = /^(#{1,4})\s+(.*)$/.exec(line)
    if (heading) {
      const level = heading[1].length
      const Tag = (level <= 2 ? 'h3' : 'h4') as 'h3' | 'h4'
      blocks.push(<Tag key={key++} className="md-h">{inline(heading[2])}</Tag>)
      i++
      continue
    }

    if (/^\s*\|.*\|\s*$/.test(line) && i + 1 < lines.length && /^\s*\|?\s*:?-{2,}/.test(lines[i + 1])) {
      const cells = (l: string) => l.trim().replace(/^\||\|$/g, '').split('|').map((c) => c.trim())
      const head = cells(line)
      const rows: string[][] = []
      i += 2
      while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) rows.push(cells(lines[i++]))
      blocks.push(
        <div key={key++} className="table-wrap md-table">
          <table className="table">
            <thead><tr>{head.map((h, j) => <th key={j}>{inline(h)}</th>)}</tr></thead>
            <tbody>{rows.map((r, j) => <tr key={j}>{r.map((c, k) => <td key={k}>{inline(c)}</td>)}</tr>)}</tbody>
          </table>
        </div>,
      )
      continue
    }

    const bullet = /^\s*[-*•]\s+/
    const numbered = /^\s*\d+[.)]\s+/
    if (bullet.test(line) || numbered.test(line)) {
      const ordered = numbered.test(line)
      const pattern = ordered ? numbered : bullet
      const items: string[] = []
      while (i < lines.length && pattern.test(lines[i])) {
        let item = lines[i].replace(pattern, '')
        i++
        // A wrapped continuation line belongs to the item above it.
        while (i < lines.length && /^\s{2,}\S/.test(lines[i]) && !bullet.test(lines[i]) && !numbered.test(lines[i])) item += ' ' + lines[i++].trim()
        items.push(item)
      }
      const List = ordered ? 'ol' : 'ul'
      blocks.push(<List key={key++}>{items.map((it, j) => <li key={j}>{inline(it)}</li>)}</List>)
      continue
    }

    const para: string[] = []
    while (i < lines.length && lines[i].trim() && !/^(#{1,4})\s/.test(lines[i]) && !bullet.test(lines[i]) && !numbered.test(lines[i])
      && !/^\s*\|.*\|\s*$/.test(lines[i])) para.push(lines[i++])
    blocks.push(<p key={key++}>{para.map((p, j) => <Fragment key={j}>{j > 0 && <br />}{inline(p)}</Fragment>)}</p>)
  }

  return <div className="md">{blocks}</div>
}

function inline(text: string): ReactNode[] {
  const out: ReactNode[] = []
  const pattern = /(\*\*[^*]+\*\*|`[^`]+`)/g
  let last = 0
  let m: RegExpExecArray | null
  let k = 0
  while ((m = pattern.exec(text))) {
    if (m.index > last) out.push(text.slice(last, m.index))
    const token = m[0]
    out.push(token.startsWith('**') ? <strong key={k++}>{token.slice(2, -2)}</strong> : <code key={k++}>{token.slice(1, -1)}</code>)
    last = m.index + token.length
  }
  if (last < text.length) out.push(text.slice(last))
  return out
}
