// src/app/vault-view/vault.model.ts

export interface VaultColumnDto {
  fieldName: string;
  columnName: string;
  clrType: string;
  isNullable: boolean;
  isPrimaryKey: boolean;
  isIndexed: boolean;
  isUnique: boolean;
  isForeignKey: boolean;
}

export interface VaultDefinitionDto {
  typeKey: string;
  clrType: string;
  clrTypeShort: string;
  keyspace: string;
  tableName: string;
  storeHistory: boolean;
  columns: VaultColumnDto[];
}

export interface VaultItemPageDto {
  typeKey: string;
  skip: number;
  take: number;
  total: number;
  fields: string[];
  items: Record<string, any>[];
}

export interface VaultBatchUpdateRequest {
  typeKey: string;
  items: Record<string, any>[];
}

export interface VaultBatchUpdateResult {
  updated: number;
}
