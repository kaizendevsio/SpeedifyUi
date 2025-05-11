// wwwroot/js/statisticsCharts.js

// Store chart instances to manage them
const charts = {};
const MAX_DATA_POINTS = 30; // Number of historical data points to show on charts

// Default colors for chart lines (add more if you expect more than 5 adapters)
const lineColors = [
    'rgba(54, 162, 235, 1)',  // Blue
    'rgba(255, 99, 132, 1)',  // Red
    'rgba(75, 192, 192, 1)',  // Green
    'rgba(255, 206, 86, 1)',  // Yellow
    'rgba(153, 102, 255, 1)', // Purple
    'rgba(255, 159, 64, 1)'   // Orange
];

// Function to initialize a new chart or update it with initial datasets
export function initializeOrUpdateChart(chartId, yAxisLabel, adapterIds, adapterNames, initialTimestamp) {
    const ctx = document.getElementById(chartId);
    if (!ctx) {
        console.error(`Chart canvas with ID ${chartId} not found.`);
        return;
    }

    const datasets = adapterIds.map((adapterId, index) => ({
        label: adapterNames[index] || adapterId, // Use adapter name or ID as label
        data: [], // Start with empty data
        borderColor: lineColors[index % lineColors.length],
        backgroundColor: lineColors[index % lineColors.length].replace('1)', '0.1)'), // For area fill if used
        tension: 0.1,
        fill: false
    }));

    if (charts[chartId]) {
        // If chart exists, update its datasets (e.g., if adapters change)
        charts[chartId].data.labels = initialTimestamp ? [initialTimestamp] : [];
        charts[chartId].data.datasets = datasets;
        charts[chartId].update();
    } else {
        // Create new chart
        charts[chartId] = new Chart(ctx, {
            type: 'line',
            data: {
                labels: initialTimestamp ? [initialTimestamp] : [], // Initial timestamp label
                datasets: datasets
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                scales: {
                    x: {
                        title: {
                            display: true,
                            text: 'Time'
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
                    duration: 200 // Smooth transition for updates
                },
                plugins: {
                    legend: {
                        position: 'top',
                    },
                    tooltip: {
                        mode: 'index',
                        intersect: false,
                    }
                }
            }
        });
    }
}

// Function to add a new data point to all datasets in a specific chart
export function addDataToChart(chartId, timestamp, dataPoints) {
    // dataPoints is an object: { adapterId1: value1, adapterId2: value2, ... }
    const chart = charts[chartId];
    if (!chart) {
        console.error(`Chart with ID ${chartId} not found for adding data.`);
        return;
    }

    // Add new timestamp label
    chart.data.labels.push(timestamp);
    if (chart.data.labels.length > MAX_DATA_POINTS) {
        chart.data.labels.shift(); // Remove oldest label
    }

    chart.data.datasets.forEach(dataset => {
        const adapterId = dataset.label; // Assuming label is set to AdapterID or a unique name
        const value = dataPoints[adapterId] !== undefined ? dataPoints[adapterId] : null; // Use null for missing data to create gaps

        dataset.data.push(value);
        if (dataset.data.length > MAX_DATA_POINTS) {
            dataset.data.shift(); // Remove oldest data point
        }
    });

    chart.update();
}

// Function to dispose of a chart
export function disposeChart(chartId) {
    if (charts[chartId]) {
        charts[chartId].destroy();
        delete charts[chartId];
        console.log(`Chart ${chartId} disposed.`);
    }
}

// Function to dispose all charts (e.g., when component is disposed)
export function disposeAllCharts() {
    for (const chartId in charts) {
        disposeChart(chartId);
    }
}
