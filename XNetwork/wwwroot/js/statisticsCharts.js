// wwwroot/js/statisticsCharts.js

// Store chart instances to manage them
const charts = {};
let maxDataPoints = 30; // Number of historical data points to show on charts
const DASHBOARD_DATA_POINTS = 30;
const LIVE_CHART_ANIMATION_DURATION = 240;
const ACTUAL_CONNECTION_ID = '__actual_connection';
const ACTUAL_CONNECTION_COLOR = '#34d399';
const DASHBOARD_CHART_PREFIX = 'dashboard-chart-';

// Dark theme colors
const GRID_COLOR = 'rgba(255, 255, 255, 0.1)';
const FONT_COLOR = '#94a3b8'; // slate-400

// Default colors for chart lines (updated for dark theme)
const lineColors = [
    '#22d3ee',  // cyan-400
    '#f472b6',  // pink-400
    '#f59e0b',  // amber-400
    '#38bdf8',  // sky-400
    '#a855f7',  // purple-400
    '#fb923c'   // orange-400
];

export function setMaxDataPoints(value) {
    const parsed = Number(value);
    maxDataPoints = Number.isFinite(parsed) ? Math.max(1, Math.round(parsed)) : 30;

    for (const chartId in charts) {
        if (isDashboardChartId(chartId)) {
            continue;
        }

        trimChartData(charts[chartId]);
        charts[chartId].update('none');
    }
}

function trimChartData(chart) {
    while (chart.data.labels.length > maxDataPoints) {
        chart.data.labels.shift();
    }

    chart.data.datasets.forEach(dataset => {
        while (dataset.data.length > maxDataPoints) {
            dataset.data.shift();
        }
    });
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
            borderDash: isActualConnection ? [6, 4] : [],
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

    if (chart.data.labels.includes(timestamp) && chart.data.labels.length > 1) {
        // If timestamp already exists and it's not the very first point, update existing points
        // This handles cases where multiple adapters report at slightly different sub-second times
        // but we group them under the same second-level timestamp.
        // console.log(`Updating data for existing timestamp ${timestamp} in chart ${chartId}`);
        const labelIndex = chart.data.labels.indexOf(timestamp);
        chart.data.datasets.forEach(dataset => {
            const adapterId = dataset.AdapterId;
            if (Object.prototype.hasOwnProperty.call(dataPointsByAdapterId, adapterId)) {
                dataset.data[labelIndex] = dataPointsByAdapterId[adapterId];
            }
        });

    } else {
        // Add new timestamp label if it doesn't exist or if it's the first point
        if (!chart.data.labels.includes(timestamp)) {
            chart.data.labels.push(timestamp);
            if (chart.data.labels.length > maxDataPoints) {
                chart.data.labels.shift(); // Remove oldest label
            }
        }

        chart.data.datasets.forEach(dataset => {
            const adapterId = dataset.AdapterId;
            const value = Object.prototype.hasOwnProperty.call(dataPointsByAdapterId, adapterId) ? dataPointsByAdapterId[adapterId] : NaN; // Use NaN for missing data

            dataset.data.push(value);
        });
    }
    trimChartData(chart);
    try {
        chart.update();
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
    for (let i = DASHBOARD_DATA_POINTS - 1; i >= 0; i--) {
        labels.push('');
    }

    try {
        charts[chartId] = new Chart(ctx, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [
                    {
                        label: 'Tunnel download',
                        data: Array(DASHBOARD_DATA_POINTS).fill(null),
                        borderColor: '#22d3ee', // cyan-400
                        backgroundColor: 'rgba(34, 211, 238, 0.1)',
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
                        data: Array(DASHBOARD_DATA_POINTS).fill(null),
                        borderColor: 'rgba(251, 146, 60, 0.62)', // orange-400
                        backgroundColor: 'rgba(251, 146, 60, 0)',
                        tension: 0.4,
                        cubicInterpolationMode: 'monotone',
                        fill: false,
                        borderWidth: 1.75,
                        pointRadius: 0,
                        pointHoverRadius: 0,
                        spanGaps: true
                    },
                    {
                        label: 'Backup download',
                        data: Array(DASHBOARD_DATA_POINTS).fill(null),
                        borderColor: 'rgba(244, 114, 182, 0.56)', // pink-400
                        backgroundColor: 'rgba(244, 114, 182, 0)',
                        tension: 0.4,
                        cubicInterpolationMode: 'monotone',
                        fill: false,
                        borderWidth: 1.75,
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

    pushDashboardValue(chart.data.datasets[0], value);
    pushDashboardValue(chart.data.datasets[1], anchorValue);
    pushDashboardValue(chart.data.datasets[2], backupValue);

    chart.data.labels.push('');
    if (chart.data.labels.length > DASHBOARD_DATA_POINTS) {
        chart.data.labels.shift();
    }

    try {
        chart.update();
        return true;
    } catch (error) {
        console.error(`Error updating sparkline ${chartId}:`, error);
        return false;
    }
}

function pushDashboardValue(dataset, value) {
    const numericValue = Number(value);
    dataset.data.push(Number.isFinite(numericValue) ? numericValue : null);
    if (dataset.data.length > DASHBOARD_DATA_POINTS) {
        dataset.data.shift();
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
