export function extractReleaseNotes(changelog, version) {
  const escaped = version.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const heading = new RegExp(`^## (?:\\[${escaped}\\]|${escaped})(?:\\s+-\\s+.+)?\\s*$`);
  const lines = String(changelog).replaceAll('\r\n', '\n').split('\n');
  const start = lines.findIndex((line) => heading.test(line));
  if (start < 0) throw new Error(`CHANGELOG.md does not contain a section for ${version}.`);
  const endOffset = lines.slice(start + 1).findIndex((line) => line.startsWith('## '));
  const end = endOffset < 0 ? lines.length : start + 1 + endOffset;
  const notes = lines.slice(start + 1, end).join('\n').trim();
  if (!notes) throw new Error(`CHANGELOG.md section ${version} is empty.`);
  return `${notes}\n`;
}
