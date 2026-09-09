// A plain Log.Error(text) call with no exception involved carries no type name (see
// LogTraceParser.Parse's fallback), so joining unconditionally would print a bare leading ": ".
export function formatErrorTitle(
    errorTypeName: string,
    message: string,
): string {
    return errorTypeName ? `${errorTypeName}: ${message}` : message;
}
