<script setup lang="ts">
/**
 * 外观快速切换 —— 明暗模式 + 强调色方案。
 *
 * 色板预览不写任何色值：每个色块自己带 `data-accent="xxx"`，
 * 于是它内部的 `var(--lme-accent)` 就是那一套方案的主色（见 tokens.css 属性选择器）。
 */
import { NPopover } from 'naive-ui'
import { useRouter } from 'vue-router'
import AppIcon from '@/components/AppIcon.vue'
import { useAppearanceStore } from '@/stores/appearance'
import { ACCENT_OPTIONS, THEME_OPTIONS } from '@/theme/preferences'

const appearance = useAppearanceStore()
const router = useRouter()

function openSettings() {
  void router.push('/settings')
}
</script>

<template>
  <NPopover trigger="click" placement="bottom-end" :show-arrow="false" :width="248" raw>
    <template #trigger>
      <button class="appearance-trigger" title="外观：主题明暗与配色方案">
        <AppIcon name="palette" :size="16" />
      </button>
    </template>

    <div class="appearance-panel">
      <div class="ap-section">
        <div class="ap-label lme-caps">主题</div>
        <div class="ap-themes">
          <button
            v-for="opt in THEME_OPTIONS"
            :key="opt.id"
            class="ap-theme"
            :class="{ active: appearance.mode === opt.id }"
            :title="opt.description"
            @click="appearance.setMode(opt.id)"
          >
            <AppIcon :name="opt.id === 'dark' ? 'moon' : 'sun'" :size="14" />
            <span>{{ opt.label }}</span>
            <AppIcon v-if="appearance.mode === opt.id" name="check" :size="13" class="ap-check" />
          </button>
        </div>
      </div>

      <div class="ap-section">
        <div class="ap-label lme-caps">强调色</div>
        <div class="ap-accents">
          <button
            v-for="opt in ACCENT_OPTIONS"
            :key="opt.id"
            class="ap-swatch"
            :class="{ active: appearance.accent === opt.id }"
            :data-accent="opt.id"
            :title="opt.label + ' —— ' + opt.description"
            @click="appearance.setAccent(opt.id)"
          >
            <span class="ap-swatch-dot" />
            <span class="ap-swatch-name">{{ opt.label }}</span>
          </button>
        </div>
      </div>

      <button class="ap-more" @click="openSettings">
        <AppIcon name="settings" :size="13" />
        <span>更多外观设置</span>
        <AppIcon name="chevronRight" :size="13" class="ap-more-arrow" />
      </button>
    </div>
  </NPopover>
</template>

<style scoped>
.appearance-trigger {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  padding: 0;
  border: none;
  border-radius: var(--lme-radius-md);
  background: none;
  color: var(--lme-text-muted);
  cursor: pointer;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard),
    color var(--lme-dur-fast) var(--lme-ease-standard);
}

.appearance-trigger:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

/* ── 面板 ── */
.appearance-panel {
  padding: var(--lme-gap-md);
  background: var(--lme-palette-bg);
  border: 1px solid var(--lme-palette-border);
  border-radius: var(--lme-radius-lg);
  box-shadow: var(--lme-shadow-lg);
}

.ap-section + .ap-section {
  margin-top: var(--lme-gap-md);
  padding-top: var(--lme-gap-md);
  border-top: 1px solid var(--lme-border-subtle);
}

.ap-label {
  margin-bottom: var(--lme-gap-sm);
}

/* ── 明暗 ── */
.ap-themes {
  display: flex;
  flex-direction: column;
  gap: var(--lme-gap-2xs);
}

.ap-theme {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  width: 100%;
  padding: 6px var(--lme-gap-sm);
  border: 1px solid transparent;
  border-radius: var(--lme-radius-md);
  background: none;
  color: var(--lme-text-secondary);
  font-family: inherit;
  font-size: var(--lme-font-size-sm);
  text-align: left;
  cursor: pointer;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.ap-theme:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.ap-theme.active {
  background: var(--lme-accent-subtle);
  border-color: var(--lme-accent-border);
  color: var(--lme-accent);
  font-weight: var(--lme-font-weight-medium);
}

.ap-check {
  margin-left: auto;
}

/* ── 强调色 ── */
.ap-accents {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: var(--lme-gap-2xs);
}

.ap-swatch {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 5px 6px;
  border: 1px solid transparent;
  border-radius: var(--lme-radius-md);
  background: none;
  color: var(--lme-text-secondary);
  font-family: inherit;
  font-size: var(--lme-font-size-xs);
  cursor: pointer;
  transition: background var(--lme-dur-fast) var(--lme-ease-standard);
}

.ap-swatch:hover {
  background: var(--lme-bg-hover);
  color: var(--lme-text-primary);
}

.ap-swatch.active {
  border-color: var(--lme-accent-border);
  background: var(--lme-accent-subtle);
  color: var(--lme-text-primary);
}

/* 色块自身的 --lme-accent 由 data-accent 决定 */
.ap-swatch-dot {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  border-radius: var(--lme-radius-full);
  background: var(--lme-accent);
  box-shadow: inset 0 0 0 1px var(--lme-bg-base);
}

.ap-swatch-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ── 底部入口 ── */
.ap-more {
  display: flex;
  align-items: center;
  gap: var(--lme-gap-sm);
  width: 100%;
  margin-top: var(--lme-gap-md);
  padding-top: var(--lme-gap-sm);
  border: none;
  border-top: 1px solid var(--lme-border-subtle);
  background: none;
  color: var(--lme-text-muted);
  font-family: inherit;
  font-size: var(--lme-font-size-xs);
  cursor: pointer;
  text-align: left;
}

.ap-more:hover {
  color: var(--lme-accent);
}

.ap-more-arrow {
  margin-left: auto;
}
</style>
