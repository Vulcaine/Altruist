import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { CacheEntryDto, CacheEntryKey } from './cache.model';

@Injectable({ providedIn: 'root' })
export class CacheDashboardService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/dashboard/v1/cache';

  /** NDJSON stream of CacheEntryDto */
  streamEntries(): Observable<CacheEntryDto> {
    return new Observable<CacheEntryDto>((observer) => {
      const xhr = new XMLHttpRequest();
      xhr.open('GET', `${this.baseUrl}/entries/stream`);
      xhr.responseType = 'text';

      let lastIndex = 0;
      let buffer = '';

      xhr.onprogress = () => {
        const text = xhr.responseText;
        buffer += text.substring(lastIndex);
        lastIndex = text.length;

        const lines = buffer.split('\n');
        buffer = lines.pop() ?? '';

        for (const line of lines) {
          const trimmed = line.trim();
          if (!trimmed) continue;

          try {
            const parsed = JSON.parse(trimmed) as CacheEntryDto;
            observer.next(parsed);
          } catch (err) {
            console.error('Failed to parse NDJSON cache entry:', trimmed, err);
          }
        }
      };

      xhr.onload = () => {
        const final = buffer.trim();
        if (final) {
          try {
            observer.next(JSON.parse(final) as CacheEntryDto);
          } catch (err) {
            console.error(
              'Failed to parse final NDJSON cache entry:',
              final,
              err
            );
          }
        }
        observer.complete();
      };

      xhr.onerror = () =>
        observer.error(xhr.statusText || 'Cache NDJSON stream error');
      xhr.send();

      return () => xhr.abort();
    });
  }

  deleteEntry(entry: CacheEntryKey): Observable<void> {
    const params = new HttpParams()
      .set('type', entry.type)
      .set('groupId', entry.groupId ?? '')
      .set('key', entry.key);

    return this.http.delete<void>(`${this.baseUrl}/entry`, { params });
  }

  updateEntry(entry: CacheEntryDto): Observable<void> {
    const body = {
      type: entry.type,
      groupId: entry.groupId,
      key: entry.key,
      value: entry.value,
    };

    return this.http.put<void>(`${this.baseUrl}/entry`, body);
  }
}
