import { NgModule } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';

import { AppRoutingModule } from './app-routing.module';
import { SceneModule } from './scene/scene-module';

@NgModule({
  imports: [BrowserModule, AppRoutingModule, SceneModule],
  providers: [],
})
export class AppModule {}
