/** bigChange P2-4: conversation minimap model — one marker per user + one merged marker per assistant reply. */
export interface MinimapMarker {
  key: string;
  kind: 'user' | 'assistant' | 'earlier';
  offsetTop: number;
  preview: string;
  active?: boolean;
  loading?: boolean;
}

export const MINIMAP_RADIUS_PX = 46;
export const MINIMAP_BOOST = 1.3;
export const MINIMAP_SNAP_PX = 24;
export const MINIMAP_POPOVER_H = 132;

export interface MinimapSource {
  key: string;
  role: 'user' | 'assistant';
  text: string;
  offsetTop: number;
}

export function buildMinimapMarkers(sources: MinimapSource[], opts?: { hasEarlier?: boolean; loadingEarlier?: boolean }): MinimapMarker[] {
  const markers: MinimapMarker[] = [];
  if (opts?.hasEarlier) {
    markers.push({ key: '__earlier', kind: 'earlier', offsetTop: 0, preview: '', loading: !!opts.loadingEarlier });
  }
  let pendingAssistant: MinimapSource | null = null;
  const flushAssistant = (): void => {
    if (!pendingAssistant) return;
    const text = (pendingAssistant.text || '').replace(/\s+/g, ' ').trim();
    if (!text) {
      pendingAssistant = null;
      return;
    }
    markers.push({
      key: pendingAssistant.key,
      kind: 'assistant',
      offsetTop: pendingAssistant.offsetTop,
      preview: text.slice(0, 280),
    });
    pendingAssistant = null;
  };
  for (const s of sources) {
    if (s.role === 'user') {
      flushAssistant();
      const text = (s.text || '').replace(/\s+/g, ' ').trim();
      if (!text) continue;
      markers.push({ key: s.key, kind: 'user', offsetTop: s.offsetTop, preview: text.slice(0, 280) });
    } else if (!pendingAssistant) {
      pendingAssistant = s;
    } else {
      const prev: MinimapSource = pendingAssistant;
      pendingAssistant = { ...prev, text: `${prev.text}\n${s.text}` };
    }
  }
  flushAssistant();
  return markers;
}

/** Active = nearest marker by scrollTop + 30% viewport (binary search over offsetTop). */
export function activeMarkerIndex(markers: MinimapMarker[], scrollTop: number, viewportH: number): number {
  if (!markers.length) return -1;
  const target = scrollTop + viewportH * 0.3;
  let lo = 0;
  let hi = markers.length - 1;
  let best = 0;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (markers[mid].offsetTop <= target) {
      best = mid;
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }
  return best;
}

export function shouldShowMinimap(markerCount: number, hasEarlier: boolean, overflowPx: number): boolean {
  if (hasEarlier) return true;
  return markerCount >= 2 && overflowPx > 1;
}

/** macOS-Dock cosine magnification; writes --magnify only (no relayout). */
export function magnifyFor(distancePx: number): number {
  if (distancePx >= MINIMAP_RADIUS_PX) return 1;
  const t = Math.cos((distancePx / MINIMAP_RADIUS_PX) * (Math.PI / 2));
  return 1 + (MINIMAP_BOOST - 1) * t;
}
