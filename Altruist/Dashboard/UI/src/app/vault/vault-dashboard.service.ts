// src/app/vault-view/vault-dashboard.service.ts

import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { VaultDefinitionDto, VaultItemPageDto } from './vault.model';

@Injectable({ providedIn: 'root' })
export class VaultDashboardService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/dashboard/v1/vaults';

  getVaults(): Observable<VaultDefinitionDto[]> {
    return this.http.get<VaultDefinitionDto[]>(this.baseUrl);
  }

  getVaultItems(
    typeKey: string,
    skip: number,
    take: number
  ): Observable<VaultItemPageDto> {
    const params = new HttpParams().set('skip', skip).set('take', take);

    return this.http.get<VaultItemPageDto>(
      `${this.baseUrl}/${encodeURIComponent(typeKey)}/items`,
      { params }
    );
  }

  batchUpdate(typeKey: string, payload: any): Observable<any> {
    return this.http.post(
      `/dashboard/v1/vaults/${encodeURIComponent(typeKey)}/batch-update`,
      payload
    );
  }
}
