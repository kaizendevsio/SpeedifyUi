// wwwroot/js/statisticsCharts.js

// Store chart instances to manage them
const charts = {};
const liveTrimTimers = {};
let maxDataPoints = 30; // Number of historical data points to show on charts
const DASHBOARD_DATA_POINTS = 30;
const LIVE_CHART_ANIMATION_DURATION = 240;
const ACTUAL_CONNECTION_ID = '__actual_connection';
const ACTUAL_CONNECTION_COLOR = '#e5e5e5';
const DASHBOARD_CHART_PREFIX = 'dashboard-chart-';
const DASHBOARD_TUNNEL_COLOR = 'rgba(126, 126, 126, 0.78)';
const DASHBOARD_ANCHOR_COLOR = 'rgba(190, 190, 190, 0.78)';
const DASHBOARD_BACKUP_COLOR = 'rgba(150, 150, 150, 0.72)';
const DASHBOARD_FILL_COLOR = 'rgba(82, 82, 82, 0.07)';

// Dark theme colors
const GRID_COLOR = 'rgba(255, 255, 255, 0.08)';
const FONT_COLOR = '#a3a3a3';

// Muted pastel series colors for Analytics. Dashboard sparklines stay monochrome.
const lineColors = [
    '#a7f3d0',
    '#fde68a',
    '#fdba74',
    '#fca5a5',
    '#c4b5fd',
    '#bae6fd'
];

function getSeriesDash(index) {
    const dashStyles = [[], [6, 4], [2, 5], [10, 4, 2, 4], [4, 2], [8, 3]];
    return dashStyles[index % dashStyles.length];
}

export function setMaxDataPoints(value) {
    const parsed = Number(value);
    maxDataPoints = Number.isFinite(parsed) ? Math.max(1, Math.round(parsed)) : 30;

    for (const chartId in charts) {
        if (isDashboardChartId(chartId)) {
            continue;
        }

        trimChartData(charts[chartId]);
        applyNumericScrollWindow(charts[chartId], maxDataPoints);
        charts[chartId].update('none');
    }
}

function trimChartData(chart, maxPoints = maxDataPoints) {
    if (chart.$xnetworkNumericScroll) {
        trimNumericScrollChartData(chart, maxPoints);
        return;
    }

    while (chart.data.labels.length > maxPoints) {
        chart.data.labels.shift();
    }

    chart.data.datasets.forEach(dataset => {
        while (dataset.data.length > maxPoints) {
            dataset.data.shift();
        }
    });
}

function trimNumericScrollChartData(chart, maxPoints) {
    const latestX = chart.$xnetworkLatestX;
    const minXToKeep = Number.isFinite(latestX)
        ? Math.max(0, latestX - maxPoints + 1)
        : 0;

    while (chart.data.labels.length > maxPoints) {
        const firstPoint = chart.data.datasets[0]?.data?.[0];
        const firstX = getPointX(firstPoint, 0);
        if (Number.isFinite(firstX) && firstX >= minXToKeep) {
            break;
        }

        chart.data.labels.shift();
        chart.data.datasets.forEach(dataset => dataset.data.shift());
    }
}

function schedulePostAnimationTrim(chartId, maxPoints) {
    if (liveTrimTimers[chartId]) {
        clearTimeout(liveTrimTimers[chartId]);
    }

    liveTrimTimers[chartId] = setTimeout(() => {
        const chart = charts[chartId];
        if (!chart) {
            return;
        }

        trimChartData(chart, maxPoints);
        chart.update('none');
        delete liveTrimTimers[chartId];
    }, LIVE_CHART_ANIMATION_DURATION + 40);
}

function configureNumericScrollChart(chart, visiblePoints, startX = 0) {
    chart.$xnetworkNumericScroll = true;
    chart.$xnetworkNextX = startX;
    chart.$xnetworkLatestX = startX > 0 ? startX - 1 : null;
    chart.$xnetworkVisiblePoints = visiblePoints;

    const xScale = chart.options.scales.x;
    xScale.type = 'linear';
    xScale.min = 0;
    xScale.max = Math.max(visiblePoints - 1, 0);
}

function reserveChartX(chart) {
    const x = Number.isFinite(chart.$xnetworkNextX)
        ? chart.$xnetworkNextX
        : chart.data.labels.length;
    chart.$xnetworkNextX = x + 1;
    chart.$xnetworkLatestX = x;
    return x;
}

function createChartPoint(x, value, missingValue = NaN) {
    return {
        x,
        y: normalizeChartValue(value, missingValue)
    };
}

function normalizeChartValue(value, missingValue = NaN) {
    const numericValue = Number(value);
    return Number.isFinite(numericValue) ? numericValue : missingValue;
}

function setPointValue(dataset, index, value, missingValue = NaN) {
    const point = dataset.data[index];
    const y = normalizeChartValue(value, missingValue);

    if (point && typeof point === 'object' && Object.prototype.hasOwnProperty.call(point, 'y')) {
        point.y = y;
        return;
    }

    dataset.data[index] = y;
}

function getPointX(point, fallbackIndex) {
    if (point && typeof point === 'object' && Number.isFinite(point.x)) {
        return point.x;
    }

    return fallbackIndex;
}

function applyNumericScrollWindow(chart, visiblePoints) {
    if (!chart.$xnetworkNumericScroll || !Number.isFinite(chart.$xnetworkLatestX)) {
        return;
    }

    const latestX = chart.$xnetworkLatestX;
    const visibleWidth = Math.max(visiblePoints - 1, 0);
    chart.options.scales.x.min = Math.max(0, latestX - visibleWidth);
    chart.options.scales.x.max = Math.max(visibleWidth, latestX);
}

function formatValueForAxis(value, yAxisLabel) {
    if (!Number.isFinite(value)) {
        return 'No data';
    }

    if (yAxisLabel.includes('(Mbps)')) {
        return `${value.toFixed(2)} Mbps`;
    }

    if (yAxisLabel.includes('(ms)')) {
        return `${value.toFixed(0)} ms`;
    }

    if (yAxisLabel.includes('(%)')) {
        return `${value.toFixed(2)}%`;
    }

    return value.toString();
}

function isDashboardChartId(chartId) {
    return chartId === 'dashboardChart' || String(chartId || '').startsWith(DASHBOARD_CHART_PREFIX);
}

// Function to initialize a new chart or update it with initial datasets
export function initializeOrUpdateChart(chartId, yAxisLabel, AdapterIds, adapterNames, initialTimestamp) {
    const ctx = document.getElementById(chartId);
    if (!ctx) {
        console.error(`Chart canvas with ID ${chartId} not found during initialization.`);
        return false; // Indicate failure
    }

    // If chart already exists, destroy it before re-creating
    // This handles cases where initialization might be called multiple times,
    // though the C# side tries to prevent this.
    if (charts[chartId]) {
        console.warn(`Chart ${chartId} already exists. Destroying and re-initializing.`);
        charts[chartId].destroy();
        delete charts[chartId];
    }

    const datasets = AdapterIds.map((AdapterId, index) => {
        const isActualConnection = AdapterId === ACTUAL_CONNECTION_ID;
        const color = isActualConnection ? ACTUAL_CONNECTION_COLOR : lineColors[index % lineColors.length];

        return {
            // Use AdapterId for internal tracking, adapterNames for display label
            label: adapterNames[index] || AdapterId,
            AdapterId: AdapterId, // Store original adapter ID for data mapping
            data: [],
            borderColor: color,
            backgroundColor: color,
            borderDash: isActualConnection ? [6, 4] : getSeriesDash(index),
            tension: 0.3, // Smooth bezier curves without exaggerated endpoint motion
            cubicInterpolationMode: 'monotone', // Smooth interpolation
            pointRadius: 0,
            pointHoverRadius: 4,
            pointHitRadius: 10,
            borderWidth: isActualConnection ? 3.5 : 2.5,
            spanGaps: true,
            fill: false
        };
    });

    try {
        charts[chartId] = new Chart(ctx, {
            type: 'line',
            data: {
                labels: initialTimestamp ? [initialTimestamp] : [],
                datasets: datasets
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                scales: {
                    x: {
                        type: 'linear',
                        title: {
                            display: false,
                            text: 'Time'
                        },
                        ticks: {
                            display: false
                        }
                    },
                    y: {
                        beginAtZero: true,
                        title: {
                            display: true,
                            text: yAxisLabel
                        }
                    }
                },
                animation: {
                    duration: LIVE_CHART_ANIMATION_DURATION,
                    easing: 'easeOutCubic'
                },
                animations: {
                    x: {
                        duration: LIVE_CHART_ANIMATION_DURATION,
                        easing: 'easeOutCubic'
                    },
                    y: {
                        duration: 0
                    }
                },
                transitions: {
                    resize: {
                        animation: {
                            duration: 0
                        }
                    }
                },
                plugins: {
                    legend: {
                        display: false  // Disabled - using custom legend in ChartCard.razor
                    },
                    tooltip: {
                        mode: 'index',
                        intersect: false,
                        callbacks: {
                            label: function(context) {
                                const label = context.dataset.label || '';
                                const yAxisLabel = context.chart.options.scales.y.title.text || '';
                                return `${label}: ${formatValueForAxis(context.parsed.y, yAxisLabel)}`;
                            }
                        }
                    }
                }
            }
        });
        configureNumericScrollChart(charts[chartId], maxDataPoints);
        console.log(`Chart ${chartId} initialized successfully.`);
        return true; // Indicate success
    } catch (error) {
        console.error(`Error creating chart ${chartId}:`, error);
        return false; // Indicate failure
    }
}

// Function to add a new data point to all datasets in a specific chart
export function addDataToChart(chartId, timestamp, dataPointsByAdapterId) {
    // dataPointsByAdapterId is an object: { "adapterID": value1, ... }
    const chart = charts[chartId];
    if (!chart) {
        // This can happen if initialization failed or was called before DOM ready.
        // console.warn(`Chart with ID ${chartId} not found for adding data. Data for timestamp ${timestamp} might be lost for this chart.`);
        return;
    }

    let addedNewPoint = false;

    const labelIndex = chart.data.labels.indexOf(timestamp);
    const hasExistingPoint = labelIndex >= 0 && chart.data.datasets.some(dataset => dataset.data.length > labelIndex);

    if (hasExistingPoint) {
        // If timestamp already exists and it's not the very first point, update existing points
        // This handles cases where multiple adapters report at slightly different sub-second times
        // but we group them under the same second-level timestamp.
        // console.log(`Updating data for existing timestamp ${timestamp} in chart ${chartId}`);
        chart.data.datasets.forEach(dataset => {
            const adapterId = dataset.AdapterId;
            if (Object.prototype.hasOwnProperty.call(dataPointsByAdapterId, adapterId)) {
                setPointValue(dataset, labelIndex, dataPointsByAdapterId[adapterId]);
            }
        });

    } else {
        // Add new timestamp label if it doesn't exist or if it's the first point
        if (labelIndex < 0) {
            chart.data.labels.push(timestamp);
        }

        addedNewPoint = true;
        const pointX = reserveChartX(chart);
        chart.data.datasets.forEach(dataset => {
            const adapterId = dataset.AdapterId;
            const value = Object.prototype.hasOwnProperty.call(dataPointsByAdapterId, adapterId) ? dataPointsByAdapterId[adapterId] : NaN; // Use NaN for missing data

            dataset.data.push(createChartPoint(pointX, value));
        });
    }
    trimChartData(chart, addedNewPoint ? maxDataPoints + 1 : maxDataPoints);
    applyNumericScrollWindow(chart, maxDataPoints);
    try {
        chart.update();
        if (addedNewPoint) {
            schedulePostAnimationTrim(chartId, maxDataPoints);
        }
    } catch (error) {
        console.error(`Error updating chart ${chartId}:`, error);
    }
}

export function initializeOrUpdateSingleSeriesChart(chartId, yAxisLabel, datasetLabel, color) {
    const ctx = document.getElementById(chartId);
    if (!ctx) {
        console.error(`Chart canvas with ID ${chartId} not found during single-series initialization.`);
        return false;
    }

    if (charts[chartId]) {
        charts[chartId].destroy();
        delete charts[chartId];
    }

    try {
        charts[chartId] = new Chart(ctx, {
            type: 'line',
            data: {
                labels: [],
                datasets: [{
                    label: datasetLabel,
                    data: [],
                    borderColor: color,
                    backgroundColor: color,
                    tension: 0.3,
                    cubicInterpolationMode: 'monotone',
                    pointRadius: 0,
                    pointHoverRadius: 4,
                    pointHitRadius: 10,
                    borderWidth: 2.5,
                    spanGaps: true,
                    fill: false
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                scales: {
                    x: {
                        title: {
                            display: false,
                            text: 'Time'
                        },
                        ticks: {
                            display: false
                        }
                    },
                    y: {
                        beginAtZero: true,
                        title: {
                            display: true,
                            text: yAxisLabel
                        }
                    }
                },
                animation: {
                    duration: LIVE_CHART_ANIMATION_DURATION,
                    easing: 'easeOutCubic'
                },
                animations: {
                    x: {
                        duration: LIVE_CHART_ANIMATION_DURATION,
                        easing: 'easeOutCubic'
                    },
                    y: {
                        duration: 0
                    }
                },
                transitions: {
                    resize: {
                        animation: {
                            duration: 0
                        }
                    }
                },
                plugins: {
                    legend: {
                        display: false
                    },
                    tooltip: {
                        mode: 'index',
                        intersect: false,
                        callbacks: {
                            label: function(context) {
                                const label = context.dataset.label || '';
                                const axisLabel = context.chart.options.scales.y.title.text || '';
                                return `${label}: ${formatValueForAxis(context.parsed.y, axisLabel)}`;
                            }
                        }
                    }
                }
            }
        });

        return true;
    } catch (error) {
        console.error(`Error creating single-series chart ${chartId}:`, error);
        return false;
    }
}

export function setSingleSeriesChartData(chartId, labels, values) {
    const chart = charts[chartId];
    if (!chart) {
        return;
    }

    chart.data.labels = Array.isArray(labels) ? labels : [];
    chart.data.datasets[0].data = Array.isArray(values) ? values : [];

    try {
        chart.update('none');
    } catch (error) {
        console.error(`Error updating single-series chart ${chartId}:`, error);
    }
}

// Function to dispose of a chart
export function disposeChart(chartId) {
    if (charts[chartId]) {
        charts[chartId].destroy();
        delete charts[chartId];
        console.log(`Chart ${chartId} disposed.`);
    }
}

// Function to initialize a dashboard sparkline chart
export function initializeDashboardSparkline(chartId) {
    const ctx = document.getElementById(chartId);
    if (!ctx) {
        console.error(`Chart canvas with ID ${chartId} not found during initialization.`);
        return false;
    }

    // If chart already exists, destroy it before re-creating
    if (charts[chartId]) {
        console.warn(`Chart ${chartId} already exists. Destroying and re-initializing.`);
        charts[chartId].destroy();
        delete charts[chartId];
    }

    // Generate initial dummy data for sparkline
    const labels = [];
    for (let i = 0; i < DASHBOARD_DATA_POINTS; i++) {
        labels.push('');
    }
    const emptySparklineData = labels.map((_, index) => ({ x: index, y: null }));

    try {
        charts[chartId] = new Chart(ctx, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [
                    {
                        label: 'Tunnel download',
                        data: emptySparklineData.map(point => ({ ...point })),
                        borderColor: DASHBOARD_TUNNEL_COLOR,
                        backgroundColor: DASHBOARD_FILL_COLOR,
                        tension: 0.4, // Smooth bezier curves
                        cubicInterpolationMode: 'monotone', // Smooth interpolation
                        fill: true,
                        borderWidth: 2,
                        pointRadius: 0,
                        pointHoverRadius: 0,
                        spanGaps: true
                    },
                    {
                        label: 'Anchor download',
                        data: emptySparklineData.map(point => ({ ...point })),
                        borderColor: DASHBOARD_ANCHOR_COLOR,
                        backgroundColor: 'rgba(190, 190, 190, 0)',
                        tension: 0.4,
                        cubicInterpolationMode: 'monotone',
                        fill: false,
                        borderWidth: 1.75,
                        borderDash: [6, 5],
                        pointRadius: 0,
                        pointHoverRadius: 0,
                        spanGaps: true
                    },
                    {
                        label: 'Backup download',
                        data: emptySparklineData.map(point => ({ ...point })),
                        borderColor: DASHBOARD_BACKUP_COLOR,
                        backgroundColor: 'rgba(150, 150, 150, 0)',
                        tension: 0.4,
                        cubicInterpolationMode: 'monotone',
                        fill: false,
                        borderWidth: 1.75,
                        borderDash: [2, 5],
                        pointRadius: 0,
                        pointHoverRadius: 0,
                        spanGaps: true
                    }
                ]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                scales: {
                    x: {
                        type: 'linear',
                        display: false,
                        grid: {
                            display: false
                        }
                    },
                    y: {
                        display: false,
                        grid: {
                            display: false
                        }
                    }
                },
                plugins: {
                    legend: {
                        display: false
                    },
                    tooltip: {
                        enabled: false
                    }
                },
                animation: {
                    duration: LIVE_CHART_ANIMATION_DURATION,
                    easing: 'easeOutCubic'
                },
                animations: {
                    x: {
                        duration: LIVE_CHART_ANIMATION_DURATION,
                        easing: 'easeOutCubic'
                    },
                    y: {
                        duration: 0
                    }
                },
                transitions: {
                    resize: {
                        animation: {
                            duration: 0
                        }
                    }
                }
            }
        });
        configureNumericScrollChart(charts[chartId], DASHBOARD_DATA_POINTS, DASHBOARD_DATA_POINTS);
        console.log(`Dashboard sparkline ${chartId} initialized successfully.`);
        return true;
    } catch (error) {
        console.error(`Error creating dashboard sparkline ${chartId}:`, error);
        return false;
    }
}

// Function to update dashboard sparkline with new data point
export function updateDashboardSparkline(chartId, value, anchorValue = null, backupValue = null) {
    const chart = charts[chartId];
    const canvas = document.getElementById(chartId);
    if (!chart || !canvas || chart.canvas !== canvas) {
        console.warn(`Chart with ID ${chartId} not found for updating sparkline.`);
        return false;
    }

    chart.data.labels.push('');
    const pointX = reserveChartX(chart);
    chart.data.datasets[0].data.push(createChartPoint(pointX, value, null));
    chart.data.datasets[1].data.push(createChartPoint(pointX, anchorValue, null));
    chart.data.datasets[2].data.push(createChartPoint(pointX, backupValue, null));
    trimChartData(chart, DASHBOARD_DATA_POINTS + 1);
    applyNumericScrollWindow(chart, DASHBOARD_DATA_POINTS);

    try {
        chart.update();
        schedulePostAnimationTrim(chartId, DASHBOARD_DATA_POINTS);
        return true;
    } catch (error) {
        console.error(`Error updating sparkline ${chartId}:`, error);
        return false;
    }
}

// Function to dispose all charts
export function disposeAllCharts() {
    for (const chartId in charts) {
        if (isDashboardChartId(chartId)) {
            continue;
        }

        disposeChart(chartId);
    }
    console.log("All charts disposed.");
}
