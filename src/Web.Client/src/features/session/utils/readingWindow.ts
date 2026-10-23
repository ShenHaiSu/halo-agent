/** bigChange P2-5: reading window — historical focus kills running lights and hides action cards. */
export interface TranscriptViewState {
  hasMoreBefore: boolean;
  hasMoreAfter: boolean;
  historical: boolean;
  loading: boolean;
}

export function isReadingWindow(view: TranscriptViewState | null | undefined): boolean {
  return !!view && view.historical === true;
}

/** transcriptRunning = isRunning && !readingWindow */
export function transcriptRunning(isRunning: boolean, view: TranscriptViewState | null | undefined): boolean {
  return isRunning && !isReadingWindow(view);
}

export function permissionVisible(isPending: boolean, view: TranscriptViewState | null | undefined): boolean {
  return isPending && !isReadingWindow(view);
}

export function outcomeVisible(isFailed: boolean, view: TranscriptViewState | null | undefined): boolean {
  return isFailed && !isReadingWindow(view);
}
