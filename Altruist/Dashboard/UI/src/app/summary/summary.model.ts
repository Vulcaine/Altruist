// src/app/summary-view/summary.model.ts

export interface ConfigEntryDto {
  key: string;
  value: string | null;
  modifiable: boolean;
}

export interface Vector3Dto {
  x: number;
  y: number;
  z: number;
}

export interface EngineInfoDto {
  diagnostics: boolean;
  framerateHz: number;
  unit: string;
  throttle: number | null;
  gravity: Vector3Dto | null;
}

export enum ServiceCategory {
  Portal = 0,
  Service = 1,
  ServiceFactory = 2,
  ServiceConfiguration = 3,
}

export interface ServiceInfoDto {
  name: string;
  fullName: string;
  assembly: string;
  category: ServiceCategory;
  lifetime?: string | null;
  serviceType?: string | null;
  endpoint?: string | null;
  context?: string | null;
}

export interface AltruistSummaryDto {
  configs: ConfigEntryDto[];
  serviceCount: number;
  services: ServiceInfoDto[];
  engine: EngineInfoDto | null;
}
