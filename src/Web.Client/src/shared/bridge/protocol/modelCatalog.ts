/** 供应商模型目录桥协议类型（单源：上游 ownedBy/created 仅展示，禁逻辑）。 */
export type CatalogModelState = 'new' | 'existing' | 'ignored';

export interface DiscoveredProviderModel {
  id: string;
  ownedBy?: string | null;
  created?: number | null;
  state: CatalogModelState;
  existsEnabled?: boolean;
}

export interface FetchProviderModelsResult {
  providerId: string;
  modelsUrl: string;
  effectiveProxySummary: string;
  elapsedMs: number;
  models: DiscoveredProviderModel[];
}

export interface ImportDiscoveredModelsRequest {
  providerId: string;
  modelIds: string[];
  importDisabled: boolean;
}
