// Verse.GenText.IsValidFilename (what CaptureFileIO.IsValidFileName / POST /api/saves check
// server-side) rejects any name over 40 characters, regardless of which characters it contains —
// the suggested default must stay under that or the very first save attempt fails.
function pad(n: number): string {
    return String(n).padStart(2, '0');
}

export function defaultSaveName(now: Date = new Date()): string {
    const stamp =
        `${String(now.getUTCFullYear())}${pad(now.getUTCMonth() + 1)}${pad(now.getUTCDate())}-` +
        `${pad(now.getUTCHours())}${pad(now.getUTCMinutes())}${pad(now.getUTCSeconds())}`;
    return `Capture_${stamp}`;
}
